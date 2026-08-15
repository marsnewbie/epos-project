using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// What a delivery costs.
/// <para>
/// A port of the RingOrder website's rules, on purpose. A shop taking web orders
/// and phone orders must quote one price, and the fastest way to break that is
/// for the two products to disagree about what a postcode prefix means.
/// </para>
/// </summary>
public class DeliveryZoneTests
{
    private static DeliveryZone Zone(
        string prefix, decimal fee, decimal min = 0, decimal freeOver = 0, bool active = true) =>
        new() { Prefix = prefix, Fee = fee, MinimumOrder = min, FreeOverAmount = freeOver, IsActive = active };

    private static UkPostcode Pc(string raw) => UkPostcode.Normalise(raw);

    private static DeliveryConfig Config(
        IEnumerable<DeliveryZone>? zones = null,
        IEnumerable<MilesBand>? bands = null,
        DeliveryMode mode = DeliveryMode.Postcode,
        decimal defaultFee = 0,
        decimal surcharge = 0,
        decimal maxMiles = 5) =>
        new()
        {
            Mode = mode,
            DefaultFee = defaultFee,
            BelowMinimumSurcharge = surcharge,
            MaxDeliveryMiles = maxMiles,
            Zones = (zones ?? []).ToList(),
            MilesBands = (bands ?? []).ToList(),
        };

    // ── Levels, not string prefixes ─────────────────────────────────────────

    [Fact]
    public void A_neighbouring_district_never_matches()
    {
        // The bug this whole design exists to prevent. B47 is Hollywood,
        // Worcestershire; B4 is the city centre. A string prefix would price one
        // as the other.
        Assert.Null(DeliveryPricing.Match([Zone("B4", 2m)], Pc("B47 5DL")));
        Assert.Null(DeliveryPricing.Match([Zone("B44", 2m)], Pc("B47 5DL")));
    }

    [Fact]
    public void A_district_rule_covers_its_whole_district()
    {
        var zones = new[] { Zone("B44", 2m) };

        Assert.NotNull(DeliveryPricing.Match(zones, Pc("B44 0QN")));
        Assert.NotNull(DeliveryPricing.Match(zones, Pc("B44 9XX")));
    }

    [Fact]
    public void A_sector_rule_covers_only_that_sector()
    {
        var zones = new[] { Zone("B44 0", 3m) };

        Assert.NotNull(DeliveryPricing.Match(zones, Pc("B44 0QN")));
        Assert.Null(DeliveryPricing.Match(zones, Pc("B44 3AB")));
    }

    [Fact]
    public void The_space_is_significant()
    {
        // "B44 0" is a sector of B44. "B440" written without a space would be a
        // district if one existed — the two must never collapse into each other.
        Assert.Equal(PostcodeRuleLevel.Sector, PostcodeRules.Parse("B44 0")!.Level);
        Assert.Equal(PostcodeRuleLevel.District, PostcodeRules.Parse("B40")!.Level);
        Assert.Equal(PostcodeRuleLevel.District, PostcodeRules.Parse("B44")!.Level);
        Assert.Equal(PostcodeRuleLevel.Area, PostcodeRules.Parse("B")!.Level);
        Assert.Equal(PostcodeRuleLevel.Unit, PostcodeRules.Parse("B44 0QN")!.Level);
    }

    [Fact]
    public void The_most_specific_rule_wins()
    {
        var zones = new[] { Zone("B", 5m), Zone("B44", 3m), Zone("B44 0", 2m), Zone("B44 0QN", 1m) };

        Assert.Equal(1m, DeliveryPricing.Match(zones, Pc("B44 0QN"))!.Fee);
        Assert.Equal(2m, DeliveryPricing.Match(zones, Pc("B44 0AA"))!.Fee);
        Assert.Equal(3m, DeliveryPricing.Match(zones, Pc("B44 9XX"))!.Fee);
        Assert.Equal(5m, DeliveryPricing.Match(zones, Pc("B23 5TT"))!.Fee);
    }

    [Fact]
    public void A_switched_off_zone_is_not_matched() =>
        Assert.Null(DeliveryPricing.Match([Zone("B44", 2m, active: false)], Pc("B44 0QN")));

    [Theory]
    [InlineData("b44")]
    [InlineData("  B44  ")]
    [InlineData("b44 ")]
    public void A_prefix_is_matched_however_it_was_typed(string prefix) =>
        Assert.NotNull(DeliveryPricing.Match([Zone(prefix, 2m)], Pc("b440qn")));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("123")]
    public void Rubbish_is_not_a_prefix(string prefix) =>
        Assert.Null(PostcodeRules.Parse(prefix));

    // ── Pricing ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_matched_zone_sets_the_fee()
    {
        var quote = DeliveryPricing.Quote(Config([Zone("B44", 2.50m)]), Pc("B44 0QN"), 18m);

        Assert.True(quote.Eligible);
        Assert.Equal(2.50m, quote.Fee);
        Assert.False(quote.NeedsAttention);
    }

    [Fact]
    public void In_postcode_mode_an_unmatched_postcode_is_outside_the_area()
    {
        // Matching the website exactly: no rule, no delivery.
        var quote = DeliveryPricing.Quote(Config([Zone("B44", 2m)], defaultFee: 3m), Pc("M1 1AE"), 18m);

        Assert.False(quote.Eligible);
        Assert.True(quote.NeedsAttention);
        Assert.Contains("outside", quote.Message);
    }

    [Fact]
    public void A_shop_with_nothing_configured_charges_what_it_always_did()
    {
        // "Nothing set up" is not "outside the delivery area", and only one of
        // those should ever stop an order.
        var quote = DeliveryPricing.Quote(Config(defaultFee: 3m), Pc("B44 0QN"), 18m);

        Assert.True(quote.Eligible);
        Assert.Equal(3m, quote.Fee);
        Assert.Equal("", quote.Message);
    }

    [Fact]
    public void Free_delivery_starts_the_moment_the_order_crosses_the_threshold()
    {
        var config = Config([Zone("B44", 2.50m, freeOver: 20m)]);

        Assert.Equal(2.50m, DeliveryPricing.Quote(config, Pc("B44 0QN"), 19.99m).Fee);
        Assert.Equal(0m, DeliveryPricing.Quote(config, Pc("B44 0QN"), 20m).Fee);
    }

    [Fact]
    public void A_free_over_of_zero_means_no_threshold_not_always_free()
    {
        // Giving one idea two spellings is how a merchant clears the box and
        // accidentally makes every delivery free.
        var quote = DeliveryPricing.Quote(Config([Zone("B44", 2.50m, freeOver: 0m)]), Pc("B44 0QN"), 500m);

        Assert.Equal(2.50m, quote.Fee);
    }

    // ── Minimums ────────────────────────────────────────────────────────────

    [Fact]
    public void The_surcharge_is_a_flat_amount_not_the_shortfall()
    {
        // The website charges a flat fee for carrying a small order. If the till
        // topped the basket up to the minimum instead, the same shop would quote
        // two different numbers for the same order.
        var config = Config([Zone("B44", 2m, min: 15m)], surcharge: 2.50m);

        var quote = DeliveryPricing.Quote(config, Pc("B44 0QN"), 5m);

        Assert.Equal(2.50m, quote.Surcharge);       // not 10.00
        Assert.Equal(4.50m, quote.TotalDeliveryCharge);
        Assert.False(quote.MeetsMinimum);
        Assert.True(quote.NeedsAttention);
        Assert.True(quote.Eligible);                // warned, never blocked
    }

    [Fact]
    public void With_no_surcharge_set_it_warns_and_charges_nothing_extra()
    {
        var quote = DeliveryPricing.Quote(Config([Zone("B44", 2m, min: 15m)]), Pc("B44 0QN"), 12m);

        Assert.Equal(0m, quote.Surcharge);
        Assert.True(quote.NeedsAttention);
    }

    [Fact]
    public void Reaching_the_minimum_exactly_is_not_under_it()
    {
        var quote = DeliveryPricing.Quote(
            Config([Zone("B44", 2m, min: 15m)], surcharge: 2m), Pc("B44 0QN"), 15m);

        Assert.True(quote.MeetsMinimum);
        Assert.Equal(0m, quote.Surcharge);
    }

    // ── Road distance ───────────────────────────────────────────────────────

    private static MilesBand Band(decimal min, decimal max, decimal fee, decimal minOrder = 0) =>
        new() { MinMiles = min, MaxMiles = max, Fee = fee, MinimumOrder = minOrder };

    [Fact]
    public void A_band_covers_its_lower_bound_and_stops_before_its_upper()
    {
        var bands = new[] { Band(0, 1, 1m), Band(1, 2, 2m), Band(2, 3, 3m) };

        Assert.Equal(1m, DeliveryPricing.MatchBand(bands, 0m)!.Fee);
        Assert.Equal(1m, DeliveryPricing.MatchBand(bands, 0.99m)!.Fee);
        Assert.Equal(2m, DeliveryPricing.MatchBand(bands, 1m)!.Fee);
        Assert.Equal(3m, DeliveryPricing.MatchBand(bands, 2.5m)!.Fee);
    }

    [Fact]
    public void Beyond_the_maximum_the_shop_does_not_deliver()
    {
        var config = Config(bands: [Band(0, 3, 2m)], mode: DeliveryMode.Miles, maxMiles: 3m);

        var quote = DeliveryPricing.Quote(config, Pc("B44 0QN"), 20m, distanceMiles: 4.2m);

        Assert.False(quote.Eligible);
        Assert.Contains("beyond", quote.Message);
    }

    [Fact]
    public void Hybrid_uses_the_postcode_rule_first_and_distance_as_the_fallback()
    {
        var config = Config(
            [Zone("B44", 2m)], [Band(0, 10, 4m)], DeliveryMode.Hybrid, maxMiles: 10m);

        var matched = DeliveryPricing.Quote(config, Pc("B44 0QN"), 20m, distanceMiles: 6m);
        Assert.Equal(2m, matched.Fee);

        var fellBack = DeliveryPricing.Quote(config, Pc("M1 1AE"), 20m, distanceMiles: 6m);
        Assert.Equal(4m, fellBack.Fee);
        Assert.Equal(6m, fellBack.DistanceMiles);
    }

    [Fact]
    public void Without_a_distance_miles_mode_says_so_rather_than_inventing_a_price()
    {
        // The router was unreachable or the postcode would not geocode. Charging
        // a made-up fee would be worse than asking someone to check.
        var config = Config(bands: [Band(0, 5, 2m)], mode: DeliveryMode.Miles, defaultFee: 3m);

        var quote = DeliveryPricing.Quote(config, Pc("B44 0QN"), 20m, distanceMiles: null);

        Assert.Equal(3m, quote.Fee);
        Assert.Contains("check", quote.Message);
    }

    [Fact]
    public void Osrm_metres_become_miles()
    {
        Assert.Equal(1m, RoadDistanceService.ParseMiles("""{"routes":[{"distance":1609.344}]}"""));

        // Zero is a real answer: the customer is at the shop's own postcode.
        Assert.Equal(0m, RoadDistanceService.ParseMiles("""{"routes":[{"distance":0}]}"""));

        Assert.Null(RoadDistanceService.ParseMiles("""{"routes":[]}"""));
        Assert.Null(RoadDistanceService.ParseMiles("""{"code":"NoRoute"}"""));
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    [Fact]
    public void Zones_are_stored_canonically_so_a_sector_stays_a_sector()
    {
        WithDb(db =>
        {
            var repo = new DeliveryZoneRepository(db);
            repo.Replace([Zone("b44 0", 3m), Zone("  b23 ", 2m), Zone("nonsense", 9m)]);

            var saved = repo.GetZones();

            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, z => z.Prefix == "B44 0");
            Assert.Contains(saved, z => z.Prefix == "B23");
        });
    }

    [Fact]
    public void A_routed_distance_is_paid_for_once()
    {
        WithDb(db =>
        {
            var repo = new MilesBandRepository(db);

            Assert.Null(repo.GetCachedMiles("B44 0QN", "B23 5TT"));

            repo.PutCachedMiles("b440qn", "b235tt", 2.4m);

            // However either postcode is spelled next time.
            Assert.Equal(2.4m, repo.GetCachedMiles("B44 0QN", "B23 5TT"));
            Assert.Equal(2.4m, repo.GetCachedMiles("b44 0qn", "B235TT"));
        });
    }

    [Fact]
    public void Zones_in_a_shop_bundle_reach_the_till()
    {
        WithDb(db =>
        {
            var zones = new DeliveryZoneRepository(db);
            var importer = new BundleImporter(
                new MenuRepository(db), new SettingsRepository(db), new StaffRepository(db),
                new PrintDeviceRepository(db), zones);

            var bundle = new ShopBundle { Shop = { Name = "Test Shop" } };
            bundle.Delivery.Zones =
            [
                new DeliveryZoneDef { Prefix = "B44", FeePence = 200, MinimumOrderPence = 1500 },
                new DeliveryZoneDef { Prefix = " b44 ", FeePence = 999 },  // the same district twice
                new DeliveryZoneDef { Prefix = "b 44", FeePence = 100 },   // not a prefix at all
            ];

            var report = importer.Import(bundle);

            var saved = Assert.Single(zones.GetZones());
            Assert.Equal("B44", saved.Prefix);
            Assert.Equal(2m, saved.Fee);        // the first one kept, not the £9.99
            Assert.Equal(15m, saved.MinimumOrder);

            Assert.Contains(report.Warnings, w => w.Contains("more than once"));
            Assert.Contains(report.Warnings, w => w.Contains("not a postcode prefix"));
        });
    }

    // ── The surcharge reaches the bill ──────────────────────────────────────

    [Fact]
    public void A_below_minimum_surcharge_is_in_the_order_total()
    {
        var order = new PosOrder
        {
            Lines = [new CartLine { Name = "Chips", Quantity = 1, BasePrice = 12m, IsAdHoc = true }],
            DeliveryFee = 2m,
            BelowMinimumSurcharge = 3m,
        };

        LinePricing.RecalculateOrder(order);

        Assert.Equal(17m, order.Total);
    }

    [Fact]
    public void What_is_taxed_is_what_is_charged()
    {
        var classes = new[] { new TaxClass { Id = "hot-food", Name = "Hot", RateBasisPoints = 2000 } };

        var order = new PosOrder
        {
            Lines = [new CartLine { Name = "Chips", Quantity = 1, BasePrice = 12m, TaxClassId = "hot-food", IsAdHoc = true }],
            DeliveryFee = 2m,
            BelowMinimumSurcharge = 3m,
        };
        LinePricing.RecalculateOrder(order);

        var bands = TaxCalculator.Summarise(order, classes);

        Assert.Equal(order.Total, Money.Round(bands.Sum(b => b.Gross)));
    }

    private static void WithDb(Action<EposDb> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ringorder-zone-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var db = new EposDb(path);
            db.Migrate();
            body(db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { path, $"{path}-wal", $"{path}-shm" })
                if (File.Exists(p)) try { File.Delete(p); } catch { /* the OS will get it */ }
        }
    }
}
