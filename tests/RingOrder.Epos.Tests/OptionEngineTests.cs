using System.Text.Json;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The option engine, exercised with the fixtures that used to be SMP-* sample
/// dishes on the demo shop's menu: required radio, optional extras, pick-two,
/// conditional reveal, and a meal deal.
/// </summary>
public class OptionEngineTests
{
    private static readonly OptionFixtures Fixtures = OptionFixtures.Load();

    private static Dictionary<string, IReadOnlyList<string>> Select(
        params (string Group, string[] Choices)[] picks) =>
        picks.ToDictionary(p => p.Group, p => (IReadOnlyList<string>)p.Choices, StringComparer.Ordinal);

    [Fact]
    public void Required_single_group_rejects_an_empty_selection()
    {
        var dish = Fixtures.Item("SMP-2");
        var error = LinePricing.ValidateSelections(dish, Select());
        Assert.NotNull(error);
    }

    [Fact]
    public void Defaults_satisfy_a_required_single_group()
    {
        var dish = Fixtures.Item("SMP-2");
        var defaults = LinePricing.DefaultSelections(dish);
        Assert.Null(LinePricing.ValidateSelections(dish, defaults));
    }

    [Fact]
    public void Optional_extras_add_their_price_deltas()
    {
        var dish = Fixtures.Item("SMP-4");
        var group = dish.OptionGroups.Single();
        var two = group.Choices.Take(2).ToArray();

        var line = LinePricing.BuildMenuLine(
            dish, 1, Select((group.Id, two.Select(c => c.Id).ToArray())));

        Assert.Equal(dish.BasePrice + two.Sum(c => c.PriceDelta), line.LineTotal);
    }

    [Fact]
    public void Pick_two_group_enforces_both_bounds()
    {
        var dish = Fixtures.Item("SMP-5");
        var group = dish.OptionGroups.Single();
        var ids = group.Choices.Select(c => c.Id).ToArray();

        Assert.NotNull(LinePricing.ValidateSelections(dish, Select((group.Id, [ids[0]]))));
        Assert.Null(LinePricing.ValidateSelections(dish, Select((group.Id, [ids[0], ids[1]]))));
        Assert.NotNull(LinePricing.ValidateSelections(dish, Select((group.Id, ids.Take(3).ToArray()))));
    }

    [Fact]
    public void Conditional_group_appears_only_for_its_trigger_choice()
    {
        var dish = Fixtures.Item("SMP-6");
        var conditional = dish.OptionGroups.Single(g => g.ShowWhen is not null);
        var trigger = conditional.ShowWhen!;
        var parent = dish.OptionGroups.Single(g => g.Id == trigger.GroupId);
        var other = parent.Choices.First(c => !trigger.ChoiceIds.Contains(c.Id));

        var hidden = LinePricing.GetVisibleOptionGroups(dish, Select((parent.Id, [other.Id])));
        Assert.DoesNotContain(hidden, g => g.Id == conditional.Id);

        var shown = LinePricing.GetVisibleOptionGroups(
            dish, Select((parent.Id, [trigger.ChoiceIds[0]])));
        Assert.Contains(shown, g => g.Id == conditional.Id);
    }

    [Fact]
    public void Hidden_group_is_not_required()
    {
        var dish = Fixtures.Item("SMP-6");
        var conditional = dish.OptionGroups.Single(g => g.ShowWhen is not null);
        Assert.True(conditional.Required);

        var parent = dish.OptionGroups.Single(g => g.Id == conditional.ShowWhen!.GroupId);
        var other = parent.Choices.First(c => !conditional.ShowWhen!.ChoiceIds.Contains(c.Id));

        // Choosing the non-triggering option must not demand the hidden group.
        Assert.Null(LinePricing.ValidateSelections(dish, Select((parent.Id, [other.Id]))));
    }

    [Fact]
    public void Quantity_multiplies_the_modified_unit_price()
    {
        var dish = Fixtures.Item("SMP-3");
        var group = dish.OptionGroups.Single();
        var dearest = group.Choices.OrderByDescending(c => c.PriceDelta).First();

        var line = LinePricing.BuildMenuLine(dish, 3, Select((group.Id, [dearest.Id])));

        Assert.Equal(Money.Round((dish.BasePrice + dearest.PriceDelta) * 3), line.LineTotal);
    }
}

/// <summary>Loads the option fixtures and resolves their shared groups.</summary>
public sealed class OptionFixtures
{
    private readonly Dictionary<string, MenuItem> _byNumber;

    private OptionFixtures(Dictionary<string, MenuItem> byNumber) => _byNumber = byNumber;

    public MenuItem Item(string menuNumber) => _byNumber[menuNumber];

    public static OptionFixtures Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "option-group-features.json");
        var doc = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path), JsonUtil.Options)!;

        var groups = doc.OptionGroups.ToDictionary(
            g => g.Id,
            g => new OptionGroup
            {
                Id = g.Id,
                Name = g.Name,
                Type = g.Type == "multi" ? OptionGroupType.Multi : OptionGroupType.Single,
                Required = g.Required,
                MinSelections = g.MinSelections,
                MaxSelections = g.MaxSelections,
                Choices = g.Choices.Select(c => new OptionChoice
                {
                    Id = c.Id,
                    Label = c.Label,
                    OptionTranslation = c.Translation,
                    PriceDelta = Money.FromPence(c.PriceDeltaPence),
                    IsDefault = c.IsDefault,
                    IsAvailable = c.IsAvailable,
                }).ToList(),
            },
            StringComparer.Ordinal);

        var items = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
        foreach (var def in doc.Items)
        {
            var item = new MenuItem
            {
                Id = def.Id,
                CategoryId = def.CategoryId,
                MenuNumber = def.MenuNumber,
                Name = def.Name,
                BasePrice = Money.FromPence(def.PricePence),
            };

            foreach (var link in def.OptionGroups)
            {
                var showWhen = link.ShowWhen is null
                    ? null
                    : new OptionShowWhen { GroupId = link.ShowWhen.GroupId, ChoiceIds = link.ShowWhen.ChoiceIds };
                item.OptionGroups.Add(groups[link.GroupId].ForItem(link.SortOrder, showWhen));
            }

            items[def.MenuNumber!] = item;
        }

        return new OptionFixtures(items);
    }

    private sealed class FixtureFile
    {
        public List<OptionGroupDef> OptionGroups { get; set; } = [];
        public List<MenuItemDef> Items { get; set; } = [];
    }
}
