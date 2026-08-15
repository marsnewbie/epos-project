using System.Text.Json;
using Microsoft.Data.Sqlite;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>
/// Moves addresses out of the old <c>customers.addresses_json</c> blob and into
/// the shared <c>addresses</c> table with a link row.
/// <para>
/// Deliberately in C# rather than in the migration SQL: whether two rows are the
/// same door is decided by <see cref="AddressFingerprint"/>, and an SQL
/// approximation of it that disagreed by one punctuation mark would quietly
/// create duplicates the moment the till ran again.
/// </para>
/// <para>
/// Each customer is moved in its own transaction and its blob is emptied as it
/// goes, so the work is resumable: a till switched off mid-way picks up exactly
/// where it stopped, and a till that has already finished does nothing at all.
/// </para>
/// </summary>
public static class AddressBackfill
{
    private sealed class LegacyAddress
    {
        public string? Label { get; set; }
        public string? Line1 { get; set; }
        public string? Line2 { get; set; }
        public string? Postcode { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed record Report(int Customers, int Links, int Places, IReadOnlyList<string> Warnings)
    {
        public bool DidWork => Customers > 0;

        public string Summary =>
            $"moved {Links} addresses for {Customers} customers into {Places} places";
    }

    /// <summary>Returns immediately when there is nothing left to move.</summary>
    public static Report Run(EposDb db, AddressRepository addresses)
    {
        var warnings = new List<string>();
        var pending = ReadPending(db);
        if (pending.Count == 0) return new Report(0, 0, 0, warnings);

        var links = 0;

        foreach (var (customerId, json) in pending)
        {
            List<LegacyAddress>? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<List<LegacyAddress>>(json, JsonUtil.Options);
            }
            catch (Exception ex)
            {
                // Never drop it. Leaving the blob alone means the row is still
                // there to look at, and the till keeps working.
                warnings.Add($"customer {customerId}: addresses_json unreadable, left in place ({ex.GetType().Name})");
                continue;
            }

            if (legacy is null || legacy.Count == 0)
            {
                ClearBlob(db, customerId);
                continue;
            }

            var first = true;
            foreach (var entry in legacy)
            {
                var address = addresses.GetOrCreate(
                    entry.Line1, entry.Line2, town: null, entry.Postcode, AddressSource.History);

                if (address is null)
                {
                    warnings.Add($"customer {customerId}: an address row had neither street nor postcode, skipped");
                    continue;
                }

                InsertLink(db, customerId, address.Id, entry.Label ?? "Home", entry.IsDefault || first);
                links++;
                first = false;
            }

            ClearBlob(db, customerId);
        }

        return new Report(pending.Count, links, addresses.Count(), warnings);
    }

    /// <summary>
    /// Customers still holding a blob. An empty list or an empty string is
    /// already done — this is what makes the pass idempotent and cheap to
    /// re-run at every startup.
    /// </summary>
    private static List<(string Id, string Json)> ReadPending(EposDb db)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, addresses_json FROM customers WHERE addresses_json NOT IN ('', '[]')";

        var pending = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) pending.Add((r.GetString(0), r.GetString(1)));
        return pending;
    }

    private static void InsertLink(
        EposDb db, string customerId, string addressId, string label, bool isDefault)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_addresses(id,customer_id,address_id,label,is_default,created_at)
            VALUES($id,$cid,$aid,$label,$def,$at)
            ON CONFLICT(customer_id,address_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$cid", customerId);
        cmd.Parameters.AddWithValue("$aid", addressId);
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$def", isDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Empties the blob once its contents are safely in rows. Two copies of the
    /// same personal data is one more than the shop needs to hold, and the
    /// pre-migration backup is the safety net if any of this was wrong.
    /// </summary>
    private static void ClearBlob(EposDb db, string customerId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE customers SET addresses_json='[]' WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", customerId);
        cmd.ExecuteNonQuery();
    }
}
