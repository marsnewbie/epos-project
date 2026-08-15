using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Erasure and retention.
/// <para>
/// Two duties that pull against each other, and both are real: UK GDPR says a
/// customer may ask to be forgotten, HMRC says the sale records behind a VAT
/// return are kept for six years. The test of the design is that a shop can obey
/// both — the identity goes, the money stays.
/// </para>
/// </summary>
public class PrivacyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-privacy-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public void Erasing_a_customer_takes_the_person_and_leaves_the_sale()
    {
        var (db, addresses, customers, retention) = NewFixture();

        var customer = new Customer { Name = "Mrs Ahmed", Phone = "0121 296 6775" };
        customers.Upsert(customer);
        customers.SaveAddress(customer, "12 Bristol Road", null, "Birmingham", "B29 6AA");
        SeedOrder(db, "order-1", customer.Id, "Mrs Ahmed", "0121 296 6775",
            "12 Bristol Road", "B29 6AA", totalPence: 2340);

        var outcome = retention.EraseCustomer(customer.Id);

        Assert.Equal(1, outcome.Customers);
        Assert.Equal(1, outcome.Links);
        Assert.Equal(1, outcome.Orders);

        Assert.Null(customers.ById(customer.Id));
        Assert.Null(customers.FindByPhone("01212966775"));

        var order = ReadOrder(db, "order-1");
        Assert.Equal(DataRetention.ErasedMarker, order.Name);
        Assert.Null(order.Phone);
        Assert.Null(order.Address);
        Assert.Null(order.Postcode);
        Assert.Null(order.CustomerId);

        // The sale itself is untouched — this is what HMRC asks the shop to keep.
        Assert.Equal(2340, order.TotalPence);
    }

    [Fact]
    public void The_delivery_map_outlives_the_customer()
    {
        // A street with nobody attached is geography, not personal data, and the
        // shop keeps a record of where it delivers.
        var (db, addresses, customers, retention) = NewFixture();

        var customer = new Customer { Name = "Mrs Ahmed", Phone = "01212966775" };
        customers.Upsert(customer);
        customers.SaveAddress(customer, "12 Bristol Road", null, "Birmingham", "B29 6AA");

        retention.EraseCustomer(customer.Id);

        Assert.Equal(1, addresses.Count());
        Assert.Equal("12 Bristol Road", addresses.ByPostcode(UkPostcode.Normalise("B29 6AA")).Single().Line1);
    }

    [Fact]
    public void An_order_taken_before_the_caller_was_saved_is_erased_too()
    {
        // Typed at the till without pressing save, so the order carries the phone
        // number but no customer_id. Leaving those behind is not an erasure.
        var (db, _, customers, retention) = NewFixture();

        var customer = new Customer { Name = "Mrs Ahmed", Phone = "0121 296 6775" };
        customers.Upsert(customer);

        SeedOrder(db, "order-loose", customerId: null, "Mrs Ahmed", "01212966775",
            "12 Bristol Road", "B29 6AA", totalPence: 1000);

        var outcome = retention.EraseCustomer(customer.Id);

        Assert.Equal(1, outcome.Orders);
        Assert.Equal(DataRetention.ErasedMarker, ReadOrder(db, "order-loose").Name);
    }

    [Fact]
    public void The_web_order_payload_is_cleared_as_well()
    {
        // The easiest thing to forget: the marketplace's raw JSON holds the name,
        // phone and address long after the columns beside it were tidied.
        var (db, _, customers, retention) = NewFixture();

        var customer = new Customer { Name = "Mrs Ahmed", Phone = "01212966775" };
        customers.Upsert(customer);
        SeedOrder(db, "order-web", customer.Id, "Mrs Ahmed", "01212966775",
            "12 Bristol Road", "B29 6AA", totalPence: 1800,
            onlinePayload: """{"customer":{"name":"Mrs Ahmed","phone":"01212966775"}}""");

        retention.EraseCustomer(customer.Id);

        Assert.Null(ReadOrder(db, "order-web").Payload);
    }

    [Fact]
    public void Another_customer_is_not_touched()
    {
        var (db, _, customers, retention) = NewFixture();

        var target = new Customer { Name = "Erase me", Phone = "01210000001" };
        var bystander = new Customer { Name = "Keep me", Phone = "01210000002" };
        customers.Upsert(target);
        customers.Upsert(bystander);
        customers.SaveAddress(bystander, "12 Bristol Road", null, null, "B29 6AA");
        SeedOrder(db, "order-other", bystander.Id, "Keep me", "01210000002", null, null, 500);

        retention.EraseCustomer(target.Id);

        Assert.NotNull(customers.ById(bystander.Id));
        Assert.Single(customers.ById(bystander.Id)!.Addresses);
        Assert.Equal("Keep me", ReadOrder(db, "order-other").Name);
    }

    // ── Retention ───────────────────────────────────────────────────────────

    [Fact]
    public void A_regular_of_ten_years_is_never_dormant()
    {
        // Retention counts from the last order, not from when the record was
        // created — otherwise every loyal customer is the first to be deleted.
        var (_, _, customers, retention) = NewFixture();

        var now = DateTimeOffset.Now;
        var customer = new Customer
        {
            Name = "Ten year regular",
            Phone = "01210000003",
            CreatedAt = now.AddYears(-10),
        };
        customers.Upsert(customer);
        customers.RecordOrder(customer.Id, now.AddDays(-3));

        Assert.Empty(retention.FindDormant(24, now));
    }

    [Fact]
    public void Someone_who_stopped_ordering_two_years_ago_is_dormant()
    {
        var (_, _, customers, retention) = NewFixture();

        var now = DateTimeOffset.Now;
        var customer = new Customer { Name = "Gone quiet", Phone = "01210000004" };
        customers.Upsert(customer);
        customers.RecordOrder(customer.Id, now.AddMonths(-30));

        var dormant = retention.FindDormant(24, now);

        Assert.Equal(customer.Id, dormant.Single().Id);
    }

    [Fact]
    public void A_retention_period_of_zero_never_selects_anybody()
    {
        // Zero is the shipped default and means "the shop has not decided yet".
        // It must never be read as "everything is expired".
        var (_, _, customers, retention) = NewFixture();

        var customer = new Customer { Name = "Ancient", Phone = "01210000005" };
        customers.Upsert(customer);
        customers.RecordOrder(customer.Id, DateTimeOffset.Now.AddYears(-9));

        Assert.Empty(retention.FindDormant(0, DateTimeOffset.Now));
    }

    [Fact]
    public void Sweeping_erases_only_the_dormant_ones()
    {
        var (_, _, customers, retention) = NewFixture();

        var now = DateTimeOffset.Now;

        var stale = new Customer { Name = "Stale", Phone = "01210000006" };
        var active = new Customer { Name = "Active", Phone = "01210000007" };
        customers.Upsert(stale);
        customers.Upsert(active);
        customers.RecordOrder(stale.Id, now.AddMonths(-40));
        customers.RecordOrder(active.Id, now.AddMonths(-1));

        var dormant = retention.FindDormant(24, now);
        var outcome = retention.Erase(dormant.Select(d => d.Id).ToList());

        Assert.Equal(1, outcome.Customers);
        Assert.Null(customers.ById(stale.Id));
        Assert.NotNull(customers.ById(active.Id));
    }

    [Fact]
    public void Erasing_nothing_is_a_no_op_rather_than_a_disaster()
    {
        var (_, _, _, retention) = NewFixture();
        Assert.Equal(ErasureOutcome.Nothing, retention.Erase([]));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private sealed record OrderRow(
        string? CustomerId, string? Name, string? Phone,
        string? Address, string? Postcode, string? Payload, long TotalPence);

    private static void SeedOrder(
        EposDb db, string id, string? customerId, string? name, string? phone,
        string? address, string? postcode, long totalPence, string? onlinePayload = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orders(id,order_number,service_type,channel,status,
                               customer_id,customer_name,customer_phone,
                               delivery_address,delivery_postcode,total_pence,online_payload,
                               created_at,updated_at)
            VALUES($id,$num,'Delivery','Phone','Paid',$cid,$name,$phone,$addr,$pc,$total,$payload,$at,$at)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$num", id);
        cmd.Parameters.AddWithValue("$cid", (object?)customerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$phone", (object?)phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addr", (object?)address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pc", (object?)postcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total", totalPence);
        cmd.Parameters.AddWithValue("$payload", (object?)onlinePayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static OrderRow ReadOrder(EposDb db, string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT customer_id, customer_name, customer_phone, delivery_address,
                   delivery_postcode, online_payload, total_pence
            FROM orders WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());

        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new OrderRow(S(0), S(1), S(2), S(3), S(4), S(5), r.GetInt64(6));
    }

    private (EposDb Db, AddressRepository Addresses, CustomerRepository Customers, DataRetention Retention)
        NewFixture()
    {
        var db = new EposDb(_dbPath);
        db.Migrate();
        var addresses = new AddressRepository(db);
        return (db, addresses, new CustomerRepository(db, addresses), new DataRetention(db));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* the OS will get it */ }
    }
}
