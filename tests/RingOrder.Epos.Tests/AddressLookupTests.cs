using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Postcode → address.
/// <para>
/// The money in this feature is in the cache, and the cache is only as good as
/// the normalising in front of it: if "b296aa" and "B29 6AA" are two keys, the
/// shop pays twice for one house.
/// </para>
/// </summary>
public class AddressLookupTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-address-{Guid.NewGuid():N}.sqlite");

    // ── Normalising ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("B29 6AA", "B29 6AA")]
    [InlineData("b296aa", "B29 6AA")]
    [InlineData("  b29   6aa  ", "B29 6AA")]
    [InlineData("b29-6aa", "B29 6AA")]
    [InlineData("EC1A 1BB", "EC1A 1BB")]
    [InlineData("ec1a1bb", "EC1A 1BB")]
    [InlineData("m11ae", "M1 1AE")]
    [InlineData("CR2 6XH", "CR2 6XH")]
    [InlineData("dn551pt", "DN55 1PT")]
    public void Any_way_a_postcode_is_typed_lands_on_one_spelling(string typed, string expected)
    {
        Assert.Equal(expected, UkPostcode.Normalise(typed).Value);
        Assert.True(UkPostcode.Normalise(typed).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ASDF")]
    [InlineData("12345")]
    [InlineData("B29 6A")]
    [InlineData("90210")]          // a US zip, typed by a confused web order
    public void Obvious_rubbish_is_refused_before_a_lookup_is_spent(string typed) =>
        Assert.False(UkPostcode.Normalise(typed).IsValid);

    [Fact]
    public void Something_too_short_to_split_is_quoted_back_as_typed()
    {
        // This string is shown to staff inside quotes when it is rejected, so a
        // trailing space from the split would be visible on the till.
        Assert.Equal("ASDF", UkPostcode.Normalise("asdf").Value);
        Assert.Equal("", UkPostcode.Normalise("").Value);
        Assert.True(UkPostcode.Normalise("").IsEmpty);
    }

    [Fact]
    public void Normalising_twice_changes_nothing()
    {
        var once = UkPostcode.Normalise("b29   6aa");
        Assert.Equal(once.Value, UkPostcode.Normalise(once.Value).Value);
    }

    [Fact]
    public void The_outward_code_is_split_off_for_delivery_banding()
    {
        var postcode = UkPostcode.Normalise("ec1a1bb");
        Assert.Equal("EC1A", postcode.Outward);
        Assert.Equal("1BB", postcode.Inward);
        Assert.Equal("EC", postcode.Area);
    }

    // ── Provider parsing ────────────────────────────────────────────────────

    [Fact]
    public void GetAddress_expanded_rows_become_candidates()
    {
        const string json = """
            {"postcode":"B29 6AA","latitude":52.44,"longitude":-1.93,
             "addresses":[
               {"line_1":"12 Bristol Road","line_2":"","line_3":"","town_or_city":"BIRMINGHAM"},
               {"line_1":"Flat 2","line_2":"14 Bristol Road","line_3":"","town_or_city":"BIRMINGHAM"}]}
            """;

        var result = GetAddressIoLookup.ParseResponse(json);

        Assert.Equal(AddressLookupStatus.Ok, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("12 Bristol Road", result.Candidates[0].StreetLine);
        Assert.Equal("Flat 2, 14 Bristol Road", result.Candidates[1].StreetLine);

        // Every candidate carries the postcode even though it was only on the envelope.
        Assert.All(result.Candidates, c => Assert.Equal("B29 6AA", c.Postcode));
        Assert.Equal(52.44, result.Latitude);
    }

    [Fact]
    public void A_post_town_in_capitals_is_cased_down_for_the_ticket()
    {
        const string json = """
            {"result":[{"postcode":"B29 6AA","line_1":"12 Bristol Road","line_2":"",
             "post_town":"BIRMINGHAM","latitude":52.44,"longitude":-1.93}],"code":2000}
            """;

        var result = IdealPostcodesLookup.ParseResponse(json);

        Assert.Equal("Birmingham", result.Candidates.Single().Town);
        Assert.Equal("B29 6AA", result.Candidates.Single().Postcode);
    }

    [Fact]
    public void A_town_the_provider_already_cased_is_left_alone()
    {
        const string json = """
            {"postcode":"B29 6AA","addresses":[{"line_1":"12 Bristol Road","town_or_city":"Sutton Coldfield"}]}
            """;

        Assert.Equal("Sutton Coldfield", GetAddressIoLookup.ParseResponse(json).Candidates.Single().Town);
    }

    [Fact]
    public void The_free_provider_confirms_the_postcode_without_offering_a_house()
    {
        // postcodes.io is Ordnance Survey open data. It has never heard of number 12,
        // and pretending otherwise would be the bug.
        const string json = """
            {"status":200,"result":{"postcode":"B29 6AA","longitude":-1.93,"latitude":52.44,
             "admin_district":"Birmingham"}}
            """;

        var result = PostcodesIoLookup.ParseResponse(json);

        Assert.Equal(AddressLookupStatus.Ok, result.Status);
        Assert.False(result.HasCandidates);
        Assert.Equal("Birmingham", result.Town);
        Assert.Equal(52.44, result.Latitude);
    }

    [Fact]
    public void An_unknown_postcode_reads_as_not_found_not_as_a_failure()
    {
        Assert.Equal(
            AddressLookupStatus.NotFound,
            PostcodesIoLookup.ParseResponse("""{"status":404,"error":"Postcode not found"}""").Status);

        Assert.Equal(
            AddressLookupStatus.NotFound,
            GetAddressIoLookup.ParseResponse("""{"postcode":"B29 6AA","addresses":[]}""").Status);
    }

    [Fact]
    public async Task With_nothing_configured_the_lookup_says_so_instead_of_throwing()
    {
        var result = await new NullAddressLookup().FindAsync(UkPostcode.Normalise("B29 6AA"));
        Assert.Equal(AddressLookupStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task A_provider_needing_a_key_it_has_not_got_asks_for_it_by_name()
    {
        var result = await new GetAddressIoLookup("").FindAsync(UkPostcode.Normalise("B29 6AA"));

        Assert.Equal(AddressLookupStatus.NotConfigured, result.Status);
        Assert.Contains("Settings", result.Message);
    }

    // ── The cache ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_house_typed_two_ways_is_paid_for_once()
    {
        var fixture = NewFixture(Found());

        await fixture.Service.FindAsync("b296aa");
        await fixture.Service.FindAsync("B29 6AA");
        await fixture.Service.FindAsync("  B29   6aa ");

        Assert.Equal(1, fixture.Provider.Calls);
    }

    [Fact]
    public async Task A_cached_answer_comes_back_whole()
    {
        var fixture = NewFixture(Found());

        await fixture.Service.FindAsync("B29 6AA");
        var second = await fixture.Service.FindAsync("B29 6AA");

        Assert.Equal("cache", second.Source);
        Assert.Equal("12 Bristol Road", second.Candidates.Single().StreetLine);
        Assert.Equal("Birmingham", second.Town);
    }

    [Fact]
    public async Task A_postcode_that_does_not_exist_is_only_asked_about_once()
    {
        // "No such postcode" is a real answer and it will not change tomorrow.
        var fixture = NewFixture(AddressLookupResult.Empty(AddressLookupStatus.NotFound, "none", "fake"));

        await fixture.Service.FindAsync("B29 6AA");
        await fixture.Service.FindAsync("B29 6AA");

        Assert.Equal(1, fixture.Provider.Calls);
    }

    [Fact]
    public async Task A_bad_minute_is_not_made_permanent()
    {
        // A timeout says nothing about the postcode. Caching it would leave the
        // shop with a wrong answer long after the broadband came back.
        var fixture = NewFixture(AddressLookupResult.Empty(AddressLookupStatus.Unavailable, "timeout", "fake"));

        await fixture.Service.FindAsync("B29 6AA");
        await fixture.Service.FindAsync("B29 6AA");

        Assert.Equal(2, fixture.Provider.Calls);
    }

    [Fact]
    public async Task Rubbish_never_reaches_the_paid_provider()
    {
        var fixture = NewFixture(Found());

        var result = await fixture.Service.FindAsync("ASDF");

        Assert.Equal(AddressLookupStatus.InvalidPostcode, result.Status);
        Assert.Equal(0, fixture.Provider.Calls);
    }

    [Fact]
    public void The_cache_counts_what_it_saved()
    {
        using var db = NewDb();
        var cache = new AddressCacheRepository(db);
        var postcode = UkPostcode.Normalise("B29 6AA");

        cache.Put(postcode, Found());
        cache.Get(postcode);
        cache.Get(postcode);

        var stats = cache.Stats();
        Assert.Equal(1, stats.Postcodes);
        Assert.Equal(2, stats.Hits);

        Assert.Equal(1, cache.Clear());
        Assert.Equal(0, cache.Stats().Postcodes);
    }

    // ── Falling back to the shop's own history ──────────────────────────────

    [Fact]
    public async Task When_the_provider_is_off_the_shop_still_knows_its_regulars()
    {
        var fixture = NewFixture(AddressLookupResult.Empty(
            AddressLookupStatus.NotConfigured, "off", "off"));

        fixture.Customers.Upsert(new Customer
        {
            Name = "Mrs Ahmed",
            Phone = "0121 296 6775",
            Addresses = [new CustomerAddress { Line1 = "12 Bristol Road", Postcode = "B29 6AA" }],
        });

        var result = await fixture.Service.FindAsync("b296aa");

        Assert.Equal("history", result.Source);
        Assert.Equal("12 Bristol Road", result.Candidates.Single().StreetLine);
    }

    [Fact]
    public async Task History_matches_however_the_address_was_typed_years_ago()
    {
        var fixture = NewFixture(AddressLookupResult.Empty(
            AddressLookupStatus.Unavailable, "down", "fake"));

        fixture.Customers.Upsert(new Customer
        {
            Name = "Old record",
            Phone = "01210000001",
            Addresses = [new CustomerAddress { Line1 = "9 Bristol Road", Postcode = "b296aa" }],
        });

        Assert.Equal("history", (await fixture.Service.FindAsync("B29 6AA")).Source);
    }

    [Fact]
    public async Task The_same_street_saved_under_two_customers_is_offered_once()
    {
        var fixture = NewFixture(AddressLookupResult.Empty(
            AddressLookupStatus.NotConfigured, "off", "off"));

        foreach (var phone in new[] { "01210000002", "01210000003" })
            fixture.Customers.Upsert(new Customer
            {
                Name = $"Customer {phone}",
                Phone = phone,
                Addresses = [new CustomerAddress { Line1 = "12 Bristol Road", Postcode = "B29 6AA" }],
            });

        Assert.Single((await fixture.Service.FindAsync("B29 6AA")).Candidates);
    }

    [Fact]
    public async Task A_mistyped_postcode_is_not_answered_from_history_either()
    {
        // Matching on a fragment would offer someone else's street to a caller
        // who fumbled the postcode.
        var fixture = NewFixture(Found());

        fixture.Customers.Upsert(new Customer
        {
            Name = "Regular",
            Phone = "01210000005",
            Addresses = [new CustomerAddress { Line1 = "12 Bristol Road", Postcode = "B29 6AA" }],
        });

        var result = await fixture.Service.FindAsync("B29");

        Assert.Equal(AddressLookupStatus.InvalidPostcode, result.Status);
        Assert.False(result.HasCandidates);
        Assert.Equal(0, fixture.Provider.Calls);
    }

    [Fact]
    public async Task A_real_answer_beats_history()
    {
        var fixture = NewFixture(Found());

        fixture.Customers.Upsert(new Customer
        {
            Name = "Regular",
            Phone = "01210000004",
            Addresses = [new CustomerAddress { Line1 = "99 Somewhere Else", Postcode = "B29 6AA" }],
        });

        var result = await fixture.Service.FindAsync("B29 6AA");

        Assert.Equal("fake", result.Source);
        Assert.Equal("12 Bristol Road", result.Candidates.Single().StreetLine);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static AddressLookupResult Found() => new()
    {
        Status = AddressLookupStatus.Ok,
        Candidates = [new AddressCandidate
        {
            Line1 = "12 Bristol Road", Town = "Birmingham", Postcode = "B29 6AA",
        }],
        Town = "Birmingham",
        Source = "fake",
        Message = "1 address found.",
    };

    private sealed class CountingLookup : IAddressLookup
    {
        private readonly AddressLookupResult _result;

        public CountingLookup(AddressLookupResult result) => _result = result;

        public int Calls { get; private set; }
        public string Name => "fake";

        public Task<AddressLookupResult> FindAsync(UkPostcode postcode, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed record Fixture(
        EposDb Db, AddressLookupService Service, CountingLookup Provider, CustomerRepository Customers);

    private EposDb NewDb()
    {
        var db = new EposDb(_dbPath);
        db.Migrate();
        return db;
    }

    private Fixture NewFixture(AddressLookupResult answer)
    {
        var db = NewDb();
        var provider = new CountingLookup(answer);
        var customers = new CustomerRepository(db);
        var service = new AddressLookupService(
            new AddressCacheRepository(db), customers, () => provider, () => true);
        return new Fixture(db, service, provider, customers);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* the OS will get it */ }
    }
}
