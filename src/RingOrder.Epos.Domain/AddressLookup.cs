using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RingOrder.Epos.Domain;

/// <summary>
/// A UK postcode, normalised.
/// <para>
/// Normalising matters more than validating. The same house gets typed as
/// "b296aa", "B29 6AA" and "B29  6aa" over a year of phone orders, and if those
/// are three different cache keys the shop pays a lookup fee three times for one
/// address. Everything downstream — the cache, the delivery zone, the customer
/// record — keys on <see cref="Value"/>.
/// </para>
/// </summary>
public readonly record struct UkPostcode
{
    private UkPostcode(string outward, string inward)
    {
        _outward = outward;
        _inward = inward;
    }

    // Held as fields rather than auto-properties because `default(UkPostcode)`
    // skips the constructor, and an uninitialised struct whose properties throw
    // on read is a landmine for every caller that declares one before assigning.
    private readonly string? _outward;
    private readonly string? _inward;

    /// <summary>The part before the space, e.g. "B29". Delivery zones band on this.</summary>
    public string Outward => _outward ?? "";

    /// <summary>The three characters after the space, e.g. "6AA".</summary>
    public string Inward => _inward ?? "";

    /// <summary>
    /// Canonical form with exactly one space, e.g. "B29 6AA". Something too short
    /// to split comes back whole rather than with a trailing space, because this
    /// string is quoted back at the user when it is rejected.
    /// </summary>
    public string Value => Inward.Length == 0 ? Outward : $"{Outward} {Inward}";

    /// <summary>The leading letters, e.g. "B" or "CV". The shop's rough catchment.</summary>
    public string Area => new(Outward.TakeWhile(char.IsLetter).ToArray());

    public bool IsEmpty => Outward.Length == 0;

    public override string ToString() => Value;

    /// <summary>
    /// Shape check against the Royal Mail pattern. This exists to stop a lookup
    /// being spent on "ASDF" — not to be the authority on what exists. A postcode
    /// that fails here is still typed into an order by hand; the provider gets the
    /// final word, and a merchant is never blocked from serving a real customer
    /// because our regex disagreed.
    /// </summary>
    private static readonly Regex Pattern = new(
        @"^(GIR 0AA|(([A-Z][0-9]{1,2})|([A-Z][A-HJ-Y][0-9]{1,2})|([A-Z][0-9][A-Z])|([A-Z][A-HJ-Y][0-9][A-Z])) [0-9][A-Z]{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips spacing and case, then puts the single space back where the Royal
    /// Mail puts it: the inward code is always the last three characters.
    /// </summary>
    public static UkPostcode Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return default;

        var packed = new string(raw
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .ToArray())
            .ToUpperInvariant();

        // Too short to split — keep it whole so the caller still sees what was
        // typed rather than a silently emptied box.
        if (packed.Length < 5) return new UkPostcode(packed, "");

        return new UkPostcode(packed[..^3], packed[^3..]);
    }

    /// <summary>True when the normalised form matches the Royal Mail shape.</summary>
    public bool IsValid => Inward.Length == 3 && Pattern.IsMatch(Value);

    public static bool TryParse(string? raw, out UkPostcode postcode)
    {
        postcode = Normalise(raw);
        return postcode.IsValid;
    }
}

/// <summary>One address a lookup offered for a postcode.</summary>
public sealed class AddressCandidate
{
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string Town { get; set; } = "";
    public string Postcode { get; set; } = "";

    // Derived, and marked so they stay out of the cache rows — a stored copy of
    // something computed is a second version of the truth waiting to disagree.

    /// <summary>
    /// What the picker shows. Blank parts drop out, so a flat without a second
    /// line does not display a stray comma.
    /// </summary>
    [JsonIgnore]
    public string Display => string.Join(", ", new[] { Line1, Line2, Town }
        .Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>What goes in the order's single address field once picked.</summary>
    [JsonIgnore]
    public string StreetLine => string.Join(", ", new[] { Line1, Line2 }
        .Where(p => !string.IsNullOrWhiteSpace(p)));
}

public enum AddressLookupStatus
{
    /// <summary>Addresses found — or, for a geography-only provider, a real postcode.</summary>
    Ok,

    /// <summary>Provider answered, and there is no such postcode.</summary>
    NotFound,

    /// <summary>Did not match the Royal Mail shape, so nothing was spent asking.</summary>
    InvalidPostcode,

    /// <summary>No provider chosen in Settings. Not an error — the shop types addresses.</summary>
    NotConfigured,

    /// <summary>Network down, key rejected, credits exhausted. Order entry carries on.</summary>
    Unavailable,
}

/// <summary>
/// What a lookup came back with. Candidates may be empty while the status is
/// <see cref="AddressLookupStatus.Ok"/> — a free geography provider confirms the
/// postcode and names the town without knowing any house numbers.
/// </summary>
public sealed class AddressLookupResult
{
    public AddressLookupStatus Status { get; init; }
    public IReadOnlyList<AddressCandidate> Candidates { get; init; } = [];

    /// <summary>Filled even when no candidates came back, when the provider knows it.</summary>
    public string Town { get; init; } = "";

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>Which provider answered, or "cache". Shown in Settings, not on the till.</summary>
    public string Source { get; init; } = "";

    /// <summary>Plain wording for the status line. Never a stack trace.</summary>
    public string Message { get; init; } = "";

    public bool HasCandidates => Candidates.Count > 0;

    public static AddressLookupResult Empty(AddressLookupStatus status, string message, string source = "") =>
        new() { Status = status, Message = message, Source = source };
}

/// <summary>
/// Turns a postcode into addresses. Implementations must not throw: a lookup is
/// a convenience beside the address box, and a till that crashes on a bad Wi-Fi
/// connection while someone is on the phone is worse than one that never offered
/// the feature.
/// </summary>
public interface IAddressLookup
{
    /// <summary>Shown in Settings so the merchant knows who is being asked.</summary>
    string Name { get; }

    Task<AddressLookupResult> FindAsync(UkPostcode postcode, CancellationToken ct = default);
}

/// <summary>Chosen in Settings. Names are stored, not enum ordinals, so a saved setting survives reordering.</summary>
public static class AddressProviderNames
{
    public const string None = "none";
    public const string PostcodesIo = "postcodesio";
    public const string GetAddressIo = "getaddress";
    public const string IdealPostcodes = "idealpostcodes";

    public static readonly string[] All = [None, PostcodesIo, GetAddressIo, IdealPostcodes];

    /// <summary>How the provider is labelled in Settings, including what it costs.</summary>
    public static string Describe(string provider) => provider switch
    {
        PostcodesIo => "postcodes.io — free, no key. Confirms the postcode and fills the town, but cannot list house numbers.",
        GetAddressIo => "getAddress.io — full addresses. Free tier for low volume, paid plans above it.",
        IdealPostcodes => "Ideal Postcodes — full addresses, pay per lookup from prepaid credits.",
        _ => "Off — addresses are typed by hand.",
    };

    /// <summary>Whether the merchant needs to paste a key before it will work.</summary>
    public static bool NeedsApiKey(string provider) =>
        provider is GetAddressIo or IdealPostcodes;
}
