using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// The places the shop has actually delivered to.
/// <para>
/// Separate from <c>address_cache</c> on purpose, because they answer different
/// questions. The cache holds everything a provider said about a postcode —
/// twenty-four doors when the shop only ever served two — and exists so the same
/// postcode is never paid for twice. This table holds the doors in use, one row
/// each however many customers share them, and is what delivery zones, dedupe
/// and a driver's map are built from.
/// </para>
/// </summary>
public sealed class AddressRepository
{
    private readonly EposDb _db;

    public AddressRepository(EposDb db) => _db = db;

    /// <summary>
    /// Finds the door or creates it. Never creates a second row for a place that
    /// differs only in spacing, punctuation or case.
    /// <para>
    /// An existing row gains coordinates the first time something supplies them,
    /// so a place first typed by hand is upgraded silently when a lookup later
    /// covers the same postcode.
    /// </para>
    /// </summary>
    public Address? GetOrCreate(
        string? line1, string? line2, string? town, string? postcode,
        AddressSource source = AddressSource.Manual,
        double? latitude = null, double? longitude = null)
    {
        var parsed = UkPostcode.Normalise(postcode);
        var street = (line1 ?? "").Trim();

        // Nothing to identify a place by is not an address, and a row of blanks
        // would collide with every other row of blanks.
        if (street.Length == 0 && parsed.IsEmpty) return null;

        var fingerprint = AddressFingerprint.For(street, line2, parsed.Value);

        using var conn = _db.Open();

        if (FindByFingerprint(conn, fingerprint) is { } existing)
        {
            if ((existing.Latitude is null || existing.Longitude is null) &&
                latitude is not null && longitude is not null)
            {
                using var enrich = conn.CreateCommand();
                enrich.CommandText = "UPDATE addresses SET latitude=$lat, longitude=$lon WHERE id=$id";
                enrich.Parameters.AddWithValue("$lat", latitude);
                enrich.Parameters.AddWithValue("$lon", longitude);
                enrich.Parameters.AddWithValue("$id", existing.Id);
                enrich.ExecuteNonQuery();
                existing.Latitude = latitude;
                existing.Longitude = longitude;
            }

            return existing;
        }

        var address = new Address
        {
            Fingerprint = fingerprint,
            Line1 = street,
            Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            Town = (town ?? "").Trim(),
            Postcode = parsed.Value,
            Outward = parsed.Outward,
            Latitude = latitude,
            Longitude = longitude,
            Source = source,
        };

        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO addresses(id,fingerprint,line1,line2,town,postcode,outward,latitude,longitude,source,created_at)
            VALUES($id,$fp,$l1,$l2,$town,$pc,$out,$lat,$lon,$src,$at)
            ON CONFLICT(fingerprint) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$id", address.Id);
        insert.Parameters.AddWithValue("$fp", address.Fingerprint);
        insert.Parameters.AddWithValue("$l1", address.Line1);
        insert.Parameters.AddWithValue("$l2", (object?)address.Line2 ?? DBNull.Value);
        insert.Parameters.AddWithValue("$town", address.Town);
        insert.Parameters.AddWithValue("$pc", address.Postcode);
        insert.Parameters.AddWithValue("$out", address.Outward);
        insert.Parameters.AddWithValue("$lat", (object?)address.Latitude ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lon", (object?)address.Longitude ?? DBNull.Value);
        insert.Parameters.AddWithValue("$src", address.Source.ToString());
        insert.Parameters.AddWithValue("$at", address.CreatedAt.ToString("o"));

        // DO NOTHING rather than an error: two tills sharing a folder, or two
        // threads on one till, may reach the same new door at the same moment.
        // Re-reading is always correct; the loser wants the winner's row.
        return insert.ExecuteNonQuery() == 1 ? address : FindByFingerprint(conn, fingerprint);
    }

    public Address? ById(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM addresses WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>
    /// Every door the shop has used at a postcode. Indexed, and reads no personal
    /// data at all — which is why the till's own-history fallback can use it
    /// without touching the customer table.
    /// </summary>
    public List<Address> ByPostcode(UkPostcode postcode)
    {
        if (!postcode.IsValid) return [];

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM addresses WHERE postcode=$pc ORDER BY line1";
        cmd.Parameters.AddWithValue("$pc", postcode.Value);

        var found = new List<Address>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) found.Add(Read(r));
        return found;
    }

    public int Count()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM addresses";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static Address? FindByFingerprint(SqliteConnection conn, string fingerprint)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM addresses WHERE fingerprint=$fp";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    internal static Address Read(SqliteDataReader r)
    {
        string S(string col) => r[col] is DBNull ? "" : Convert.ToString(r[col])!;
        string? SN(string col) => r[col] is DBNull ? null : Convert.ToString(r[col]);
        double? D(string col) => r[col] is DBNull ? null : Convert.ToDouble(r[col]);

        return new Address
        {
            Id = S("id"),
            Fingerprint = S("fingerprint"),
            Line1 = S("line1"),
            Line2 = SN("line2"),
            Town = S("town"),
            Postcode = S("postcode"),
            Outward = S("outward"),
            Latitude = D("latitude"),
            Longitude = D("longitude"),
            Source = Enum.TryParse<AddressSource>(S("source"), out var src) ? src : AddressSource.Manual,
            CreatedAt = DateTimeOffset.TryParse(S("created_at"), out var at) ? at : DateTimeOffset.Now,
        };
    }
}
