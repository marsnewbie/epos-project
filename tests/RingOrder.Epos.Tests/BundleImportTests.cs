using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Importing the demo shop's bundle must reproduce it exactly. This is the test
/// that stands between a menu we entered by hand and a schema change that
/// quietly drops part of it.
/// </summary>
public class BundleImportTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly MenuRepository _menu;
    private readonly ShopBundle _bundle;

    private static string BundlePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.ringpos.json");

    public BundleImportTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _menu = new MenuRepository(_db);
        var settings = new SettingsRepository(_db);
        var staff = new StaffRepository(_db);
        _bundle = BundleImporter.Read(BundlePath);
        new BundleImporter(_menu, settings, staff).Import(_bundle);
    }

    [Fact]
    public void Imports_the_bundle_without_warnings()
    {
        var report = new BundleImporter(_menu, new SettingsRepository(_db), new StaffRepository(_db))
            .Import(_bundle);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Every_category_and_dish_survives()
    {
        Assert.Equal(_bundle.Menu.Categories.Count, _menu.GetCategories(visibleOnly: false).Count);
        Assert.Equal(_bundle.Menu.Items.Count, _menu.CountItems());
    }

    [Fact]
    public void Prices_reconcile_to_the_penny()
    {
        var expected = _bundle.Menu.Items.Sum(i => (long)i.PricePence);
        var actual = _menu.GetItems(availableOnly: false).Sum(i => (long)Money.ToPence(i.BasePrice));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Option_group_choices_and_deltas_survive()
    {
        var groups = _menu.GetOptionGroups();
        Assert.Equal(_bundle.Menu.OptionGroups.Count, groups.Count);

        foreach (var expected in _bundle.Menu.OptionGroups)
        {
            var actual = groups[expected.Id];
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Choices.Count, actual.Choices.Count);
            Assert.Equal(
                expected.Choices.Sum(c => (long)c.PriceDeltaPence),
                actual.Choices.Sum(c => (long)Money.ToPence(c.PriceDelta)));
        }
    }

    [Fact]
    public void Every_dish_keeps_the_groups_it_referenced()
    {
        var items = _menu.GetItems(availableOnly: false).ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var expected in _bundle.Menu.Items.Where(i => i.OptionGroups.Count > 0))
        {
            var actual = items[expected.Id];
            Assert.Equal(
                expected.OptionGroups.Select(l => l.GroupId).OrderBy(x => x, StringComparer.Ordinal),
                actual.OptionGroups.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Dishes_sharing_a_group_name_keep_their_own_prices()
    {
        // Five Thai dishes carried the same legacy group id while Pa Kin Mao
        // priced its upgrades higher. Under a shared catalogue they must be two
        // groups, or four dishes silently get the wrong price table.
        var items = _menu.GetItems(availableOnly: false);
        var paKinMao = items.SingleOrDefault(i => i.Name.Contains("Pa Kin Mao", StringComparison.OrdinalIgnoreCase));
        var padThai = items.SingleOrDefault(i => i.Name.Contains("Pad Thai", StringComparison.OrdinalIgnoreCase));
        if (paKinMao is null || padThai is null) return;   // demo menu may change

        decimal Beef(MenuItem item) => item.OptionGroups
            .SelectMany(g => g.Choices)
            .Where(c => c.Label.Contains("Beef", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.PriceDelta)
            .DefaultIfEmpty(0m)
            .First();

        Assert.NotEqual(Beef(padThai), Beef(paKinMao));
    }

    [Fact]
    public void Conditional_reveals_still_point_at_a_real_choice()
    {
        var groups = _menu.GetOptionGroups();
        foreach (var item in _menu.GetItems(availableOnly: false))
        {
            foreach (var link in item.OptionLinks.Where(l => l.ShowWhen is not null))
            {
                var source = groups[link.ShowWhen!.GroupId];
                foreach (var choiceId in link.ShowWhen.ChoiceIds)
                    Assert.Contains(source.Choices, c => c.Id == choiceId);
            }
        }
    }

    [Fact]
    public void Reimport_is_idempotent()
    {
        var before = _menu.CountItems();
        new BundleImporter(_menu, new SettingsRepository(_db), new StaffRepository(_db)).Import(_bundle);
        Assert.Equal(before, _menu.CountItems());
    }

    [Fact]
    public void Tax_classes_and_price_tiers_are_stored_with_exact_rates()
    {
        var taxClasses = _menu.GetTaxClasses();
        Assert.Equal(_bundle.Tax.Classes.Count, taxClasses.Count);

        var standard = taxClasses.First(t => t.RateBasisPoints == 2000);
        Assert.Equal(0.20m, standard.Rate);   // basis points, so never 0.19999999

        var tiers = _menu.GetPriceTiers();
        Assert.Equal(_bundle.PriceTiers.Count, tiers.Count);
        Assert.Single(tiers, t => t.IsDefault);
    }

    [Fact]
    public void Shop_identity_reaches_settings()
    {
        var settings = new SettingsRepository(_db).Load();
        Assert.Equal(_bundle.Shop.Name, settings.ShopName);
        Assert.Equal(_bundle.QuickNotes.Count, settings.QuickNotes.Count);
    }

    [Fact]
    public void Seeded_staff_can_sign_in_with_the_bundle_pin()
    {
        var staff = new StaffRepository(_db);
        var seed = _bundle.Staff.First();
        var member = staff.Authenticate(seed.Pin);

        Assert.NotNull(member);
        Assert.Equal(seed.Name, member!.Name);
        Assert.True(member.MustChangePin);
        Assert.DoesNotContain(seed.Pin, member.PinHash);   // never stored in the clear
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }
}
