using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// A place and a person's relationship to it are different things.
/// <para>
/// The split is what lets one customer keep several addresses, lets two
/// customers share one door without storing it twice, and lets a customer be
/// erased while the shop keeps a delivery map that never named anybody.
/// </para>
/// </summary>
public class AddressBookTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-addrbook-{Guid.NewGuid():N}.sqlite");

    // ── The fingerprint ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Flat 2, 14 Bristol Rd.", "FLAT 2  14 BRISTOL RD")]
    [InlineData("12 High Street", "12  high   street")]
    [InlineData("60 Warren Farm Road", "60, Warren Farm Road")]
    public void One_door_spelled_two_ways_has_one_identity(string a, string b) =>
        Assert.Equal(
            AddressFingerprint.For(a, null, "B29 6AA"),
            AddressFingerprint.For(b, null, "b296aa"));

    [Fact]
    public void Different_flats_in_one_building_stay_different()
    {
        Assert.NotEqual(
            AddressFingerprint.For("Flat 2", "14 Bristol Road", "B29 6AA"),
            AddressFingerprint.For("Flat 3", "14 Bristol Road", "B29 6AA"));

        // The second line is part of the identity, not decoration.
        Assert.NotEqual(
            AddressFingerprint.For("Flat 2", "14 Bristol Road", "B29 6AA"),
            AddressFingerprint.For("Flat 2", "16 Bristol Road", "B29 6AA"));
    }

    // ── Places ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_door_is_stored_once_however_it_is_typed()
    {
        var (_, addresses, _) = NewFixture();

        var first = addresses.GetOrCreate("Flat 2, 14 Bristol Rd.", null, "Birmingham", "B29 6AA");
        var again = addresses.GetOrCreate("FLAT 2  14 BRISTOL RD", null, "Birmingham", "b296aa");

        Assert.Equal(first!.Id, again!.Id);
        Assert.Equal(1, addresses.Count());
    }

    [Fact]
    public void A_place_is_stored_with_its_postcode_normalised_and_its_outward_split_off()
    {
        var (_, addresses, _) = NewFixture();

        var place = addresses.GetOrCreate("12 High Street", null, "Birmingham", "  b29   6aa ")!;

        Assert.Equal("B29 6AA", place.Postcode);
        Assert.Equal("B29", place.Outward);
    }

    [Fact]
    public void A_place_first_typed_by_hand_gains_coordinates_when_a_lookup_supplies_them()
    {
        var (_, addresses, _) = NewFixture();

        addresses.GetOrCreate("12 High Street", null, "Birmingham", "B29 6AA");
        var enriched = addresses.GetOrCreate(
            "12 High Street", null, "Birmingham", "B29 6AA",
            AddressSource.Lookup, latitude: 52.44, longitude: -1.93)!;

        Assert.Equal(52.44, enriched.Latitude);
        Assert.Equal(1, addresses.Count());
        Assert.Equal(52.44, addresses.ById(enriched.Id)!.Latitude);
    }

    [Fact]
    public void Something_with_neither_street_nor_postcode_is_not_a_place()
    {
        var (_, addresses, _) = NewFixture();

        Assert.Null(addresses.GetOrCreate("  ", null, null, ""));
        Assert.Equal(0, addresses.Count());
    }

    // ── Links ───────────────────────────────────────────────────────────────

    [Fact]
    public void Two_customers_at_one_address_share_the_row()
    {
        var (_, addresses, customers) = NewFixture();

        var one = Save(customers, "Flatmate one", "01210000001", "12 Bristol Road", "B29 6AA");
        var two = Save(customers, "Flatmate two", "01210000002", "12 Bristol Road", "B29 6AA");

        Assert.Equal(1, addresses.Count());
        Assert.Equal(
            customers.ById(one.Id)!.Addresses.Single().AddressId,
            customers.ById(two.Id)!.Addresses.Single().AddressId);
    }

    [Fact]
    public void One_customer_can_keep_several_addresses()
    {
        var (_, _, customers) = NewFixture();

        var customer = Save(customers, "Mr Okafor", "01210000003", "12 Bristol Road", "B29 6AA");
        customers.SaveAddress(customer, "40 Warren Farm Road", null, "Birmingham", "B44 0QN",
            label: "Work");

        var loaded = customers.ById(customer.Id)!;

        Assert.Equal(2, loaded.Addresses.Count);
        Assert.Contains(loaded.Addresses, a => a.Label == "Work");
    }

    [Fact]
    public void Saving_the_same_address_twice_does_not_duplicate_the_link()
    {
        var (_, _, customers) = NewFixture();

        var customer = Save(customers, "Repeat", "01210000004", "12 Bristol Road", "B29 6AA");
        customers.SaveAddress(customer, "12 Bristol Road", null, null, "B29 6AA");
        customers.SaveAddress(customer, "12  bristol road", null, null, "b296aa");

        Assert.Single(customers.ById(customer.Id)!.Addresses);
    }

    [Fact]
    public void Only_one_address_is_the_default()
    {
        var (_, _, customers) = NewFixture();

        var customer = Save(customers, "Mover", "01210000005", "12 Bristol Road", "B29 6AA");
        customers.SaveAddress(customer, "40 Warren Farm Road", null, null, "B44 0QN",
            makeDefault: true);

        var loaded = customers.ById(customer.Id)!;

        Assert.Single(loaded.Addresses, a => a.IsDefault);
        Assert.Equal("40 Warren Farm Road", loaded.DefaultAddress!.Line1);
    }

    [Fact]
    public void A_driver_note_belongs_to_the_household_not_the_street()
    {
        var (_, addresses, customers) = NewFixture();

        var customer = Save(customers, "Notes", "01210000006", "12 Bristol Road", "B29 6AA");
        customers.SaveAddress(customer, "12 Bristol Road", null, null, "B29 6AA",
            notes: "Ring the bell twice");

        var link = customers.ById(customer.Id)!.Addresses.Single();
        Assert.Equal("Ring the bell twice", link.Notes);

        // The place itself carries nothing personal.
        var place = addresses.ById(link.AddressId)!;
        Assert.Equal("12 Bristol Road", place.Line1);
    }

    // ── Moving the old blob across ──────────────────────────────────────────

    [Fact]
    public void Addresses_stored_the_old_way_survive_the_upgrade()
    {
        // A v4-shaped customer row: addresses as a JSON blob on the customer.
        using var db = new EposDb(_dbPath);
        db.Migrate();

        SeedLegacyCustomer(db, "cust-1", "Mrs Ahmed", "01212966775", """
            [{"label":"Home","line1":"12 Bristol Road","postcode":"b296aa","isDefault":true},
             {"label":"Work","line1":"40 Warren Farm Road","postcode":"B44 0QN","isDefault":false}]
            """);

        var addresses = new AddressRepository(db);
        var report = AddressBackfill.Run(db, addresses);

        Assert.Equal(1, report.Customers);
        Assert.Equal(2, report.Links);
        Assert.Empty(report.Warnings);

        var customers = new CustomerRepository(db, addresses);
        var loaded = customers.ById("cust-1")!;

        Assert.Equal(2, loaded.Addresses.Count);
        Assert.Equal("B29 6AA", loaded.DefaultAddress!.Postcode);   // normalised on the way through
        Assert.Contains(loaded.Addresses, a => a.Label == "Work");
    }

    [Fact]
    public void Running_the_move_again_does_nothing()
    {
        using var db = new EposDb(_dbPath);
        db.Migrate();
        SeedLegacyCustomer(db, "cust-1", "A", "01210000007",
            """[{"line1":"12 Bristol Road","postcode":"B29 6AA"}]""");

        var addresses = new AddressRepository(db);
        Assert.True(AddressBackfill.Run(db, addresses).DidWork);
        Assert.False(AddressBackfill.Run(db, addresses).DidWork);

        Assert.Single(new CustomerRepository(db, addresses).ById("cust-1")!.Addresses);
    }

    [Fact]
    public void Two_old_customers_at_one_door_end_up_sharing_one_place()
    {
        using var db = new EposDb(_dbPath);
        db.Migrate();
        SeedLegacyCustomer(db, "cust-1", "A", "01210000008",
            """[{"line1":"12 Bristol Road","postcode":"B29 6AA"}]""");
        SeedLegacyCustomer(db, "cust-2", "B", "01210000009",
            """[{"line1":"12  BRISTOL ROAD","postcode":"b29 6aa"}]""");

        var addresses = new AddressRepository(db);
        var report = AddressBackfill.Run(db, addresses);

        Assert.Equal(2, report.Links);
        Assert.Equal(1, addresses.Count());
    }

    [Fact]
    public void Unreadable_legacy_json_is_left_alone_rather_than_dropped()
    {
        using var db = new EposDb(_dbPath);
        db.Migrate();
        SeedLegacyCustomer(db, "cust-1", "A", "01210000010", "{ not json at all");

        var report = AddressBackfill.Run(db, addresses: new AddressRepository(db));

        Assert.Single(report.Warnings);
        Assert.Equal(0, report.Links);

        // Still there to look at, not silently discarded.
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT addresses_json FROM customers WHERE id='cust-1'";
        Assert.Equal("{ not json at all", cmd.ExecuteScalar());
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static Customer Save(
        CustomerRepository customers, string name, string phone, string line1, string postcode)
    {
        var customer = new Customer { Name = name, Phone = phone };
        customers.Upsert(customer);
        customers.SaveAddress(customer, line1, null, "Birmingham", postcode);
        return customer;
    }

    internal static void SeedLegacyCustomer(EposDb db, string id, string name, string phone, string json)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customers(id,name,phone,phone_digits,addresses_json,created_at,updated_at)
            VALUES($id,$n,$p,$pd,$aj,$at,$at)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$p", phone);
        cmd.Parameters.AddWithValue("$pd", CustomerRepository.NormalizePhone(phone));
        cmd.Parameters.AddWithValue("$aj", json);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private (EposDb Db, AddressRepository Addresses, CustomerRepository Customers) NewFixture()
    {
        var db = new EposDb(_dbPath);
        db.Migrate();
        var addresses = new AddressRepository(db);
        return (db, addresses, new CustomerRepository(db, addresses));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* the OS will get it */ }
    }
}
