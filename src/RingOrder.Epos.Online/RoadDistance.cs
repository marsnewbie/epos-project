using System.Globalization;
using System.Text.Json;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Online;

/// <summary>
/// Road miles between two postcodes.
/// <para>
/// The same two free services the RingOrder website uses: postcodes.io to turn a
/// postcode into coordinates, and the public OSRM router for the driving
/// distance between them. Deliberately the same pair — a shop pricing delivery
/// by distance on its website and on the till must get the same number, and two
/// different routing engines will not agree.
/// </para>
/// <para>
/// Road distance rather than straight line because that is what a driver drives.
/// A river or a railway between two points can double the journey while the
/// straight line says three-quarters of a mile.
/// </para>
/// <para>
/// The public OSRM instance is a demo with no SLA, so nothing here depends on it
/// being up: every answer is cached forever by the caller, and a failure returns
/// null rather than throwing. A shop keeps quoting from its cache indefinitely.
/// </para>
/// </summary>
public sealed class RoadDistanceService
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly HttpClient _http;

    public RoadDistanceService(HttpClient? http = null) => _http = http ?? Shared;

    /// <summary>Miles by road, or null when either postcode or the router failed.</summary>
    public async Task<decimal?> MilesBetweenAsync(
        string originPostcode, string customerPostcode, CancellationToken ct = default)
    {
        var origin = await GeocodeAsync(originPostcode, ct).ConfigureAwait(false);
        if (origin is null) return null;

        var customer = await GeocodeAsync(customerPostcode, ct).ConfigureAwait(false);
        if (customer is null) return null;

        return await DriveMilesAsync(origin.Value, customer.Value, ct).ConfigureAwait(false);
    }

    public async Task<(double Lat, double Lon)?> GeocodeAsync(
        string postcode, CancellationToken ct = default)
    {
        var parsed = UkPostcode.Normalise(postcode);
        if (!parsed.IsValid) return null;

        try
        {
            var url = $"https://api.postcodes.io/postcodes/{Uri.EscapeDataString(parsed.Value)}";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
                return null;

            if (!result.TryGetProperty("latitude", out var lat) ||
                !result.TryGetProperty("longitude", out var lon))
                return null;

            return (lat.GetDouble(), lon.GetDouble());
        }
        catch
        {
            // Pricing a delivery must never be the thing that stops an order.
            return null;
        }
    }

    private async Task<decimal?> DriveMilesAsync(
        (double Lat, double Lon) from, (double Lat, double Lon) to, CancellationToken ct)
    {
        try
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://router.project-osrm.org/route/v1/driving/{0},{1};{2},{3}?overview=false",
                from.Lon, from.Lat, to.Lon, to.Lat);

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseMiles(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Metres out of an OSRM route response, in miles. Null when it said nothing useful.</summary>
    public static decimal? ParseMiles(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("routes", out var routes) ||
            routes.ValueKind != JsonValueKind.Array ||
            routes.GetArrayLength() == 0)
            return null;

        if (!routes[0].TryGetProperty("distance", out var distance) ||
            distance.ValueKind != JsonValueKind.Number)
            return null;

        var metres = distance.GetDouble();

        // Zero is a real answer — the customer is at the shop's own postcode — so
        // this checks for a finite number rather than for truthiness.
        if (double.IsNaN(metres) || double.IsInfinity(metres) || metres < 0) return null;

        return Math.Round((decimal)(metres / 1609.344), 2);
    }
}
