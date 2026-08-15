using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

/// <summary>What an erasure actually removed, for the audit line and the screen.</summary>
public sealed record ErasureOutcome(int Customers, int Links, int Orders)
{
    public static readonly ErasureOutcome Nothing = new(0, 0, 0);

    public string Summary =>
        $"{Customers} customers, {Links} saved addresses, {Orders} orders de-identified";
}

/// <summary>One dormant record, described without repeating what makes it personal.</summary>
public sealed record DormantCustomer(string Id, DateTimeOffset LastSeen);

/// <summary>
/// Erasure and retention for the phone book.
/// <para>
/// Two obligations pull in opposite directions and both are real. UK GDPR says
/// personal data is not kept longer than the purpose needs, and a customer may
/// ask to be forgotten. HMRC says the sale records behind a VAT return are kept
/// for six years. They are reconciled by erasing the *identity* and keeping the
/// *transaction*: an order holds its money, its VAT treatment and whether it was
/// delivered, with the name, phone, address and web payload removed.
/// </para>
/// <para>
/// The <c>addresses</c> table is untouched by erasure. A street and a postcode
/// with nobody attached is geography, and the shop keeps a delivery map it never
/// needed a name to build.
/// </para>
/// </summary>
public sealed class DataRetention
{
    /// <summary>What replaces a name, so a blank is never mistaken for a lost record.</summary>
    public const string ErasedMarker = "[erased]";

    private readonly EposDb _db;

    public DataRetention(EposDb db) => _db = db;

    /// <summary>
    /// Customers with no order — or no contact at all — since the cutoff.
    /// <para>
    /// Counts from the last order rather than from when the record was made: a
    /// regular of ten years is the opposite of stale.
    /// </para>
    /// </summary>
    public List<DormantCustomer> FindDormant(int months, DateTimeOffset now)
    {
        if (months <= 0) return [];

        var cutoff = now.AddMonths(-months);

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, COALESCE(last_order_at, updated_at, created_at) AS last_seen
            FROM customers
            WHERE COALESCE(last_order_at, updated_at, created_at) < $cutoff
            ORDER BY last_seen
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("o"));

        var dormant = new List<DormantCustomer>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            dormant.Add(new DormantCustomer(
                r.GetString(0),
                DateTimeOffset.TryParse(r.GetString(1), out var seen) ? seen : cutoff));

        return dormant;
    }

    public ErasureOutcome EraseCustomer(string customerId) => Erase([customerId]);

    /// <summary>
    /// Erases a set of customers in one transaction: either the whole request
    /// lands or none of it does, so a crash cannot leave an order pointing at a
    /// customer row that no longer exists.
    /// </summary>
    public ErasureOutcome Erase(IReadOnlyCollection<string> customerIds)
    {
        if (customerIds.Count == 0) return ErasureOutcome.Nothing;

        var ids = new HashSet<string>(customerIds, StringComparer.Ordinal);

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Phone numbers as well as ids, because an order taken before the caller
        // was saved to the phone book carries the number but no customer_id, and
        // an erasure that left those behind would not be an erasure.
        var phones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(400))
        {
            var names = chunk.Select((_, i) => $"$p{i}").ToArray();
            using var read = conn.CreateCommand();
            read.Transaction = tx;
            read.CommandText =
                $"SELECT phone_digits FROM customers WHERE id IN ({string.Join(",", names)})";
            for (var i = 0; i < chunk.Length; i++)
                read.Parameters.AddWithValue(names[i], chunk[i]);

            using var r = read.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0) && r.GetString(0).Length > 0)
                    phones.Add(r.GetString(0));
        }

        var orderIds = FindOrders(conn, tx, ids, phones);
        var orders = DeIdentifyOrders(conn, tx, orderIds);

        var links = 0;
        var customers = 0;
        foreach (var chunk in ids.Chunk(400))
        {
            var names = chunk.Select((_, i) => $"$c{i}").ToArray();
            var list = string.Join(",", names);

            using (var dropLinks = conn.CreateCommand())
            {
                dropLinks.Transaction = tx;
                dropLinks.CommandText = $"DELETE FROM customer_addresses WHERE customer_id IN ({list})";
                for (var i = 0; i < chunk.Length; i++)
                    dropLinks.Parameters.AddWithValue(names[i], chunk[i]);
                links += dropLinks.ExecuteNonQuery();
            }

            using (var dropCustomers = conn.CreateCommand())
            {
                dropCustomers.Transaction = tx;
                dropCustomers.CommandText = $"DELETE FROM customers WHERE id IN ({list})";
                for (var i = 0; i < chunk.Length; i++)
                    dropCustomers.Parameters.AddWithValue(names[i], chunk[i]);
                customers += dropCustomers.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return new ErasureOutcome(customers, links, orders);
    }

    private static List<string> FindOrders(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        HashSet<string> customerIds,
        HashSet<string> phoneDigits)
    {
        // Scanned rather than joined because the stored phone is as it was typed
        // — "0121 296 6775" and "01212966775" are the same customer, and only
        // stripping the punctuation in code gets that right.
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, customer_id, customer_phone FROM orders";

        var matched = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var orderId = r.GetString(0);
            var customerId = r.IsDBNull(1) ? null : r.GetString(1);
            var phone = r.IsDBNull(2) ? null : r.GetString(2);

            if (customerId is not null && customerIds.Contains(customerId))
            {
                matched.Add(orderId);
                continue;
            }

            if (phone is not null &&
                phoneDigits.Contains(CustomerRepository.NormalizePhone(phone)))
                matched.Add(orderId);
        }

        return matched;
    }

    /// <summary>
    /// Strips identity from an order and keeps the sale.
    /// <para>
    /// <c>online_payload</c> is cleared too, and it is the one most easily
    /// forgotten: a web order stores the marketplace's whole JSON, name, phone
    /// and address included, long after the structured columns were tidied.
    /// </para>
    /// <para>
    /// Free-text order notes are left alone. They are operational — "no MSG",
    /// "allergy: peanuts" — and blanking them would erase kitchen instructions on
    /// live tickets. <c>docs/PRIVACY.md</c> says so plainly, because a merchant
    /// answering an erasure request needs to know what was and was not touched.
    /// </para>
    /// </summary>
    private static int DeIdentifyOrders(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        List<string> orderIds)
    {
        var changed = 0;

        foreach (var chunk in orderIds.Chunk(400))
        {
            var names = chunk.Select((_, i) => $"$o{i}").ToArray();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE orders SET
                  customer_id       = NULL,
                  customer_name     = $marker,
                  customer_phone    = NULL,
                  delivery_address  = NULL,
                  delivery_postcode = NULL,
                  online_payload    = NULL
                WHERE id IN ({string.Join(",", names)})
                """;
            cmd.Parameters.AddWithValue("$marker", ErasedMarker);
            for (var i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue(names[i], chunk[i]);

            changed += cmd.ExecuteNonQuery();
        }

        return changed;
    }
}
