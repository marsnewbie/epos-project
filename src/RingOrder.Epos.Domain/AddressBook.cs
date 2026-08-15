namespace RingOrder.Epos.Domain;

/// <summary>Where an address came into the till from. Kept for provenance, not for billing.</summary>
public enum AddressSource
{
    Manual,
    Lookup,
    History,
    Web,
}

/// <summary>
/// A place.
/// <para>
/// Deliberately holds no name, no phone and no order history — a street and a
/// postcode are public geography, and on their own they identify a building
/// rather than a person. That separation is the point: it is the *link* between
/// a place and a customer that is personal data, so erasing a customer can
/// remove every link while the shop keeps a delivery map it is entitled to.
/// </para>
/// <para>
/// Rows are shared. Two flatmates ordering separately, a household that changed
/// its phone number, a couple with two accounts — one row, referenced twice.
/// </para>
/// </summary>
public sealed class Address
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Normalised identity used to avoid storing the same door twice.</summary>
    public string Fingerprint { get; set; } = "";

    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string Town { get; set; } = "";

    /// <summary>Always the normalised form — see <see cref="UkPostcode"/>.</summary>
    public string Postcode { get; set; } = "";

    /// <summary>Split out and indexed: delivery zones band on the outward code.</summary>
    public string Outward { get; set; } = "";

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public AddressSource Source { get; set; } = AddressSource.Manual;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>One line for a ticket: the building and street, without the town.</summary>
    public string StreetLine => string.Join(", ", new[] { Line1, Line2 }
        .Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>What a picker shows.</summary>
    public string Display => string.Join(", ", new[] { Line1, Line2, Town }
        .Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// The identity of a door, reduced to something two spellings of it agree on.
/// <para>
/// "Flat 2, 14 Bristol Rd." and "FLAT 2 14 BRISTOL RD" are the same place and
/// must not become two rows — the whole value of a shared address table is that
/// it deduplicates. Only letters and digits survive, so punctuation, spacing and
/// case cannot create a second copy.
/// </para>
/// <para>
/// It is not a hash and is not meant to be one: it stays readable so a support
/// session can see why two rows did or did not merge.
/// </para>
/// </summary>
public static class AddressFingerprint
{
    public static string For(string? line1, string? line2, string? postcode) =>
        $"{Squash(line1)}|{Squash(line2)}|{Squash(postcode)}";

    public static string For(Address address) =>
        For(address.Line1, address.Line2, address.Postcode);

    private static string Squash(string? value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

/// <summary>
/// A customer's link to a place, and everything about that link that belongs to
/// the person rather than the building.
/// <para>
/// This is the personal data. "Ring the bell twice, the dog barks" is about a
/// household, not a street, and it goes when the customer does.
/// </para>
/// </summary>
public sealed class CustomerAddress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerId { get; set; } = "";
    public string AddressId { get; set; } = "";

    /// <summary>"Home", "Work", "Mum's" — the customer's own word for it.</summary>
    public string Label { get; set; } = "Home";

    /// <summary>Directions for the driver. Personal to the household.</summary>
    public string? Notes { get; set; }

    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Drives retention: an address nobody has used in years is not needed.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Resolved when the customer is loaded.</summary>
    public Address? Address { get; set; }

    // Reading through to the place, so a caller that only wants to print a line
    // does not join by hand. Writes go through the repository, which is what
    // keeps one door as one row.

    public string Line1 => Address?.Line1 ?? "";
    public string? Line2 => Address?.Line2;
    public string Town => Address?.Town ?? "";
    public string Postcode => Address?.Postcode ?? "";
    public string StreetLine => Address?.StreetLine ?? "";

    /// <summary>Label first, because that is how someone on the phone refers to it.</summary>
    public string Display => Address is null
        ? Label
        : $"{Label}: {Address.Display}";
}
