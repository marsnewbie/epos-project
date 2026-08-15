using System.Globalization;
using System.Net;
using System.Text.Json;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Online;

/// <summary>
/// Postcode lookup providers.
/// <para>
/// There is no free source of UK house numbers. Every service that can turn
/// "B29 6AA" into a list of front doors is reselling the Royal Mail Postcode
/// Address File, and the Royal Mail charges for it. What is genuinely free —
/// postcodes.io — is Ordnance Survey open data: it confirms a postcode exists
/// and names the district, but it has never heard of number 12.
/// </para>
/// <para>
/// So the shop chooses. Off is a real answer and the default. postcodes.io costs
/// nothing and still catches the wrong-postcode phone call. A PAF reseller fills
/// the house number in, at a few pence a lookup — which the cache in front of
/// these providers turns into a few pence *per postcode, once, ever*.
/// </para>
/// </summary>
public static class AddressLookupFactory
{
    public static IAddressLookup Create(string provider, string apiKey, HttpClient? http = null) =>
        provider switch
        {
            AddressProviderNames.PostcodesIo => new PostcodesIoLookup(http),
            AddressProviderNames.GetAddressIo => new GetAddressIoLookup(apiKey, http),
            AddressProviderNames.IdealPostcodes => new IdealPostcodesLookup(apiKey, http),
            _ => new NullAddressLookup(),
        };
}

/// <summary>Nothing configured. Says so calmly instead of failing.</summary>
public sealed class NullAddressLookup : IAddressLookup
{
    public string Name => "off";

    public Task<AddressLookupResult> FindAsync(UkPostcode postcode, CancellationToken ct = default) =>
        Task.FromResult(AddressLookupResult.Empty(
            AddressLookupStatus.NotConfigured,
            "Postcode lookup is switched off — type the address.",
            Name));
}

/// <summary>
/// Shared plumbing. The timeout is short on purpose: a member of staff is on the
/// phone with a customer waiting, and four seconds of nothing is the point at
/// which typing the address by hand is faster than waiting for the answer.
/// </summary>
public abstract class HttpAddressLookup : IAddressLookup
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(4) };

    protected HttpAddressLookup(HttpClient? http) => Http = http ?? Shared;

    protected HttpClient Http { get; }

    public abstract string Name { get; }

    protected abstract string BuildUrl(UkPostcode postcode);

    protected abstract AddressLookupResult Parse(string json);

    /// <summary>
    /// Whether this provider needs credentials the merchant has not supplied.
    /// Checked before the request so an unconfigured key reads as "paste your
    /// key in Settings" rather than as a mysterious 401.
    /// </summary>
    protected virtual string? MissingConfiguration => null;

    public async Task<AddressLookupResult> FindAsync(UkPostcode postcode, CancellationToken ct = default)
    {
        if (!postcode.IsValid)
            return AddressLookupResult.Empty(
                AddressLookupStatus.InvalidPostcode,
                $"\"{postcode.Value}\" is not a UK postcode.",
                Name);

        if (MissingConfiguration is { } missing)
            return AddressLookupResult.Empty(AddressLookupStatus.NotConfigured, missing, Name);

        try
        {
            using var response = await Http.GetAsync(BuildUrl(postcode), ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Describe(response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return AddressLookupResult.Empty(
                AddressLookupStatus.Unavailable, "Lookup timed out — type the address.", Name);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. This runs while an order is being taken;
            // the only useful behaviour is to step out of the way.
            return AddressLookupResult.Empty(
                AddressLookupStatus.Unavailable, $"Lookup unavailable ({ex.GetType().Name}).", Name);
        }
    }

    /// <summary>
    /// Turns a status code into something a shop owner can act on. "402" means
    /// nothing to them; "your lookup credits have run out" tells them what to do.
    /// </summary>
    private AddressLookupResult Describe(HttpStatusCode code) => code switch
    {
        HttpStatusCode.NotFound => AddressLookupResult.Empty(
            AddressLookupStatus.NotFound, "No addresses found for that postcode.", Name),
        HttpStatusCode.BadRequest => AddressLookupResult.Empty(
            AddressLookupStatus.InvalidPostcode, "That postcode was rejected as malformed.", Name),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AddressLookupResult.Empty(
            AddressLookupStatus.Unavailable, "The lookup API key was rejected — check it in Settings.", Name),
        HttpStatusCode.PaymentRequired => AddressLookupResult.Empty(
            AddressLookupStatus.Unavailable, "Lookup credits have run out — top up with the provider.", Name),
        (HttpStatusCode)429 => AddressLookupResult.Empty(
            AddressLookupStatus.Unavailable, "Lookup limit reached for now — type the address.", Name),
        _ => AddressLookupResult.Empty(
            AddressLookupStatus.Unavailable, $"Lookup service returned {(int)code}.", Name),
    };

    /// <summary>
    /// PAF stores post towns in capitals. "BIRMINGHAM" on a delivery ticket reads
    /// as shouting, so it is cased down — but only when the provider sent it in
    /// caps, leaving anything already mixed-case alone.
    /// </summary>
    protected static string TidyTown(string? town)
    {
        var value = (town ?? "").Trim();
        if (value.Length == 0 || value.Any(char.IsLower)) return value;
        return CultureInfo.GetCultureInfo("en-GB").TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    protected static string Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : "";

    protected static double? Number(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// postcodes.io — free, open source, self-hostable, no key, Ordnance Survey data.
/// <para>
/// It returns no addresses, and that is not a defect to work around: it is the
/// honest limit of open data. What it does give is worth having on its own — the
/// postcode is confirmed real before a driver is sent, the town fills itself in,
/// and the coordinates are what delivery-zone banding will price against.
/// </para>
/// </summary>
public sealed class PostcodesIoLookup : HttpAddressLookup
{
    public PostcodesIoLookup(HttpClient? http = null) : base(http) { }

    public override string Name => "postcodes.io";

    protected override string BuildUrl(UkPostcode postcode) =>
        $"https://api.postcodes.io/postcodes/{Uri.EscapeDataString(postcode.Value)}";

    protected override AddressLookupResult Parse(string json) => ParseResponse(json, Name);

    public static AddressLookupResult ParseResponse(string json, string source = "postcodes.io")
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object)
            return AddressLookupResult.Empty(
                AddressLookupStatus.NotFound, "No such postcode.", source);

        var town = TidyTown(Text(result, "admin_district"));

        return new AddressLookupResult
        {
            Status = AddressLookupStatus.Ok,
            Candidates = [],
            Town = town,
            Latitude = Number(result, "latitude"),
            Longitude = Number(result, "longitude"),
            Source = source,
            Message = town.Length > 0
                ? $"Postcode confirmed — {town}. Type the house number and street."
                : "Postcode confirmed. Type the house number and street.",
        };
    }
}

/// <summary>getAddress.io — PAF reseller. Free tier for low volume, paid above it.</summary>
public sealed class GetAddressIoLookup : HttpAddressLookup
{
    private readonly string _apiKey;

    public GetAddressIoLookup(string apiKey, HttpClient? http = null) : base(http) =>
        _apiKey = apiKey.Trim();

    public override string Name => "getAddress.io";

    protected override string? MissingConfiguration =>
        _apiKey.Length == 0 ? "getAddress.io needs an API key — add it in Settings." : null;

    protected override string BuildUrl(UkPostcode postcode) =>
        $"https://api.getaddress.io/find/{Uri.EscapeDataString(postcode.Value)}" +
        $"?api-key={Uri.EscapeDataString(_apiKey)}&expand=true";

    protected override AddressLookupResult Parse(string json) => ParseResponse(json, Name);

    public static AddressLookupResult ParseResponse(string json, string source = "getAddress.io")
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var candidates = new List<AddressCandidate>();
        var postcode = Text(root, "postcode");

        if (root.TryGetProperty("addresses", out var addresses) &&
            addresses.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in addresses.EnumerateArray())
            {
                // Without expand=true this array is plain comma-joined strings.
                // Handle both so a proxy or an older key does not come back empty.
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var joined = entry.GetString()!;
                    var parts = joined.Split(',', StringSplitOptions.TrimEntries)
                        .Where(p => p.Length > 0).ToArray();
                    if (parts.Length == 0) continue;
                    candidates.Add(new AddressCandidate
                    {
                        Line1 = parts[0],
                        Line2 = parts.Length > 2 ? parts[1] : null,
                        Town = TidyTown(parts.Length > 1 ? parts[^1] : ""),
                        Postcode = postcode,
                    });
                    continue;
                }

                var line1 = Text(entry, "line_1");
                if (line1.Length == 0) continue;

                var line2 = Text(entry, "line_2");
                var line3 = Text(entry, "line_3");
                var extra = string.Join(", ", new[] { line2, line3 }.Where(p => p.Length > 0));

                candidates.Add(new AddressCandidate
                {
                    Line1 = line1,
                    Line2 = extra.Length > 0 ? extra : null,
                    Town = TidyTown(Text(entry, "town_or_city")),
                    Postcode = postcode,
                });
            }
        }

        return Build(candidates, postcode, Number(root, "latitude"), Number(root, "longitude"), source);
    }

    internal static AddressLookupResult Build(
        List<AddressCandidate> candidates, string postcode, double? lat, double? lon, string source)
    {
        if (candidates.Count == 0)
            return AddressLookupResult.Empty(
                AddressLookupStatus.NotFound, "No addresses found for that postcode.", source);

        foreach (var candidate in candidates.Where(c => c.Postcode.Length == 0))
            candidate.Postcode = postcode;

        return new AddressLookupResult
        {
            Status = AddressLookupStatus.Ok,
            Candidates = candidates,
            Town = candidates[0].Town,
            Latitude = lat,
            Longitude = lon,
            Source = source,
            Message = candidates.Count == 1
                ? "1 address found."
                : $"{candidates.Count} addresses found.",
        };
    }
}

/// <summary>Ideal Postcodes — PAF distributor, pay-as-you-go from prepaid credits.</summary>
public sealed class IdealPostcodesLookup : HttpAddressLookup
{
    private readonly string _apiKey;

    public IdealPostcodesLookup(string apiKey, HttpClient? http = null) : base(http) =>
        _apiKey = apiKey.Trim();

    public override string Name => "Ideal Postcodes";

    protected override string? MissingConfiguration =>
        _apiKey.Length == 0 ? "Ideal Postcodes needs an API key — add it in Settings." : null;

    protected override string BuildUrl(UkPostcode postcode) =>
        $"https://api.ideal-postcodes.co.uk/v1/postcodes/{Uri.EscapeDataString(postcode.Value)}" +
        $"?api_key={Uri.EscapeDataString(_apiKey)}";

    protected override AddressLookupResult Parse(string json) => ParseResponse(json, Name);

    public static AddressLookupResult ParseResponse(string json, string source = "Ideal Postcodes")
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return AddressLookupResult.Empty(
                AddressLookupStatus.NotFound, "No addresses found for that postcode.", source);

        var candidates = new List<AddressCandidate>();
        double? lat = null, lon = null;

        foreach (var entry in result.EnumerateArray())
        {
            var line1 = Text(entry, "line_1");
            if (line1.Length == 0) continue;

            var line2 = Text(entry, "line_2");
            var line3 = Text(entry, "line_3");
            var extra = string.Join(", ", new[] { line2, line3 }.Where(p => p.Length > 0));

            lat ??= Number(entry, "latitude");
            lon ??= Number(entry, "longitude");

            candidates.Add(new AddressCandidate
            {
                Line1 = line1,
                Line2 = extra.Length > 0 ? extra : null,
                Town = TidyTown(Text(entry, "post_town")),
                Postcode = Text(entry, "postcode"),
            });
        }

        return GetAddressIoLookup.Build(candidates, "", lat, lon, source);
    }
}
