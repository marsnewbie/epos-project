using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// What a delivery costs.
/// <para>
/// Priced by postcode prefix because that is how a takeaway publishes its area —
/// the leaflet says "B44, B23 — £2", not "£1 per mile". The rules here decide
/// what a customer is charged, so they are tested away from any screen.
/// </para>
/// </summary>
public class DeliveryZoneTests
{
    private static DeliveryZone Zone(
        string prefix, decimal fee, decimal min = 0, decimal freeOver = 0, bool deliverable = true) =>
        new()
        {
            Prefix = prefix,
            Fee = fee,
            MinimumOrder = min,
            FreeOverAmount = freeOver,
            IsDeliverable = deliverable,
        };

    private static UkPostcode Pc(string raw) => UkPostcode.Normalise(raw);

    // ── Matching ────────────────────────────────────────────────────────────

    [Fact]
    public void The_longest_matching_prefix_wins()
    {
        // B44 and B4 are different districts. A shop that has defined both means
        // the specific one for B44 0QN.
        var zones = new[] { Zone("B", 4m), Zone("B4", 3m), Zone("B44", 2m) };

        Assert.Equal("B44", DeliveryPricing.Match(zones, Pc("B44 0QN"))!.Prefix);
        Assert.Equal("B4", DeliveryPricing.Match(zones, Pc("B4 7AP"))!.Prefix);
        Assert.Equal("B", DeliveryPricing.Match(zones, Pc("B23 5TT"))!.Prefix);
    }

    [Fact]
    public void A_broad_prefix_catches_a_district_the_shop_never_listed()
    {
        // Deliberate: a shop that writes "B4" and no "B44" means that side of
        // town, and charging the broader zone beats declaring a real customer
        // unreachable. The matched zone is shown on screen so this is visible.
        var zones = new[] { Zone("B4", 3m) };

        Assert.Equal("B4", DeliveryPricing.Match(zones, Pc("B44 0QN"))!.Prefix);
    }

    [Fact]
    public void A_prefix_can_narrow_to_a_sector()
    {
        // "B440" is the awkward end of B44 and costs more to reach.
        var zones = new[] { Zone("B44", 2m), Zone("B440", 3.5m) };

        Assert.Equal(3.5m, DeliveryPricing.Match(zones, Pc("B44 0QN"))!.Fee);
        Assert.Equal(2m, DeliveryPricing.Match(zones, Pc("B44 9XX"))!.Fee);
    }

    [Theory]
    [InlineData("b44")]
    [InlineData("B 44")]
    [InlineData("b-44")]
    public void A_prefix_is_matched_however_it_was_typed(string prefix) =>
        Assert.NotNull(DeliveryPricing.Match([Zone(prefix, 2m)], Pc("b440qn")));

    [Fact]
    public void No_zone_matches_a_postcode_from_another_town() =>
        Assert.Null(DeliveryPricing.Match([Zone("B44", 2m)], Pc("M1 1AE")));

    // ── Pricing ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_matched_zone_sets_the_fee()
    {
        var quote = DeliveryPricing.Quote([Zone("B44", 2.50m)], Pc("B44 0QN"), 18m, defaultFee: 9m);

        Assert.Equal(2.50m, quote.Fee);
        Assert.Equal("B44", quote.Zone!.Prefix);
        Assert.False(quote.NeedsAttention);
    }

    [Fact]
    public void Free_delivery_starts_the_moment_the_order_crosses_the_threshold()
    {
        var zones = new[] { Zone("B44", 2.50m, freeOver: 20m) };

        Assert.Equal(2.50m, DeliveryPricing.Quote(zones, Pc("B44 0QN"), 19.99m, 9m).Fee);
        Assert.Equal(0m, DeliveryPricing.Quote(zones, Pc("B44 0QN"), 20m, 9m).Fee);
    }

    [Fact]
    public void A_shop_with_no_zones_charges_what_it_always_did()
    {
        // No zones is not "outside the area" — it is a shop that has not set any
        // up, and it must behave exactly as it did before this feature existed.
        var quote = DeliveryPricing.Quote([], Pc("B44 0QN"), 18m, defaultFee: 3m);

        Assert.Equal(3m, quote.Fee);
        Assert.False(quote.OutsideArea);
        Assert.Equal("", quote.Message);
    }

    [Fact]
    public void An_unmatched_postcode_falls_back_or_is_refused_as_the_shop_chose()
    {
        var zones = new[] { Zone("B44", 2m) };

        var fallback = DeliveryPricing.Quote(zones, Pc("M1 1AE"), 18m, 3m,
            outside: OutsideZonePolicy.ChargeDefault);
        Assert.Equal(3m, fallback.Fee);
        Assert.False(fallback.OutsideArea);

        var refused = DeliveryPricing.Quote(zones, Pc("M1 1AE"), 18m, 3m,
            outside: OutsideZonePolicy.Refuse);
        Assert.True(refused.OutsideArea);
        Assert.True(refused.NeedsAttention);
    }

    [Fact]
    public void A_zone_the_shop_will_not_deliver_to_says_so()
    {
        var quote = DeliveryPricing.Quote(
            [Zone("B44", 0m, deliverable: false)], Pc("B44 0QN"), 30m, 3m);

        Assert.True(quote.OutsideArea);
        Assert.Equal(0m, quote.Fee);
        Assert.Contains("does not deliver", quote.Message);
    }

    // ── Minimums ────────────────────────────────────────────────────────────

    [Fact]
    public void Under_the_minimum_warns_without_charging_anything_extra()
    {
        var quote = DeliveryPricing.Quote(
            [Zone("B44", 2m, min: 15m)], Pc("B44 0QN"), 12m, 3m,
            belowMinimum: BelowMinimumPolicy.Warn);

        Assert.Equal(3m, quote.Shortfall);
        Assert.Equal(0m, quote.Surcharge);
        Assert.True(quote.NeedsAttention);
    }

    [Fact]
    public void The_surcharge_policy_tops_the_order_up_to_the_minimum()
    {
        var quote = DeliveryPricing.Quote(
            [Zone("B44", 2m, min: 15m)], Pc("B44 0QN"), 12m, 3m,
            belowMinimum: BelowMinimumPolicy.Surcharge);

        Assert.Equal(3m, quote.Surcharge);
        Assert.Contains("added", quote.Message);
    }

    [Fact]
    public void Reaching_the_minimum_exactly_is_not_under_it()
    {
        var quote = DeliveryPricing.Quote(
            [Zone("B44", 2m, min: 15m)], Pc("B44 0QN"), 15m, 3m);

        Assert.Equal(0m, quote.Shortfall);
        Assert.False(quote.NeedsAttention);
    }

    [Fact]
    public void An_order_with_no_postcode_yet_still_has_a_fee()
    {
        // Someone starts ringing dishes before asking where it is going. The
        // ticket must still total up.
        var quote = DeliveryPricing.Quote([Zone("B44", 2m)], Pc(""), 18m, defaultFee: 3m);

        Assert.Equal(3m, quote.Fee);
        Assert.False(quote.NeedsAttention);
    }

    // ── The surcharge reaches the bill ──────────────────────────────────────

    [Fact]
    public void A_below_minimum_surcharge_is_in_the_order_total()
    {
        // It was being included in the VAT calculation and left out of the total,
        // so a web order carrying one had tax worked out on money the customer
        // was never charged.
        var order = new PosOrder
        {
            Lines = [new CartLine { Name = "Chips", Quantity = 1, BasePrice = 12m, IsAdHoc = true }],
            DeliveryFee = 2m,
            BelowMinimumSurcharge = 3m,
        };

        LinePricing.RecalculateOrder(order);

        Assert.Equal(12m, order.Subtotal);
        Assert.Equal(17m, order.Total);
    }

    // ── Storage and provisioning ────────────────────────────────────────────

    [Fact]
    public void Zones_survive_a_save_and_reload_with_their_prefixes_normalised()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ringorder-zones-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var db = new EposDb(path);
            db.Migrate();
            var repo = new DeliveryZoneRepository(db);

            repo.Replace(
            [
                Zone("b44", 2m, min: 15m, freeOver: 25m),
                Zone("B 23", 3m),
                new DeliveryZone { Prefix = "  ", Fee = 9m },   // a row still being typed
            ]);

            var saved = repo.GetZones();

            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, z => z.Prefix == "B44" && z.MinimumOrder == 15m && z.FreeOverAmount == 25m);
            Assert.Contains(saved, z => z.Prefix == "B23");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { path, $"{path}-wal", $"{path}-shm" })
                if (File.Exists(p)) try { File.Delete(p); } catch { /* the OS will get it */ }
        }
    }

    [Fact]
    public void Editing_zones_replaces_the_set_rather_than_adding_to_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ringorder-zones2-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var db = new EposDb(path);
            db.Migrate();
            var repo = new DeliveryZoneRepository(db);

            repo.Replace([Zone("B44", 2m), Zone("B23", 3m)]);
            repo.Replace([Zone("B44", 2.5m)]);          // B23 removed on screen

            var saved = repo.GetZones();
            Assert.Equal("B44", Assert.Single(saved).Prefix);
            Assert.Equal(2.5m, saved[0].Fee);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { path, $"{path}-wal", $"{path}-shm" })
                if (File.Exists(p)) try { File.Delete(p); } catch { /* the OS will get it */ }
        }
    }

    [Fact]
    public void Zones_in_a_shop_bundle_reach_the_till()
    {
        // The bundle has carried a zones list since the schema rebuild and
        // nothing read it — every shop was charged the one flat default.
        var path = Path.Combine(Path.GetTempPath(), $"ringorder-zonesimp-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var db = new EposDb(path);
            db.Migrate();

            var zones = new DeliveryZoneRepository(db);
            var settings = new SettingsRepository(db);
            var importer = new BundleImporter(
                new MenuRepository(db), settings, new StaffRepository(db),
                new PrintDeviceRepository(db), zones);

            var bundle = new ShopBundle { Shop = { Name = "Test Shop" } };
            bundle.Delivery.DefaultFeePence = 300;
            bundle.Delivery.Zones =
            [
                new DeliveryZoneDef { Prefix = "B44", FeePence = 200, MinimumOrderPence = 1500 },
                new DeliveryZoneDef { Prefix = "b 44", FeePence = 999 },   // the same area twice
                new DeliveryZoneDef { Prefix = "", FeePence = 100 },       // nothing to match on
            ];

            var report = importer.Import(bundle);

            var saved = Assert.Single(zones.GetZones());
            Assert.Equal("B44", saved.Prefix);
            Assert.Equal(2m, saved.Fee);
            Assert.Equal(15m, saved.MinimumOrder);

            Assert.Contains(report.Warnings, w => w.Contains("more than once"));
            Assert.Contains(report.Warnings, w => w.Contains("no postcode prefix"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { path, $"{path}-wal", $"{path}-shm" })
                if (File.Exists(p)) try { File.Delete(p); } catch { /* the OS will get it */ }
        }
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

        // The gross the VAT was worked out on must be the total the customer pays.
        Assert.Equal(order.Total, Money.Round(bands.Sum(b => b.Gross)));
    }
}
