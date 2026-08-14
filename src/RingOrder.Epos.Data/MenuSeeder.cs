using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Data;

public sealed class MenuSeeder
{
    private readonly MenuRepository _menu;
    private readonly SettingsRepository _settings;

    public MenuSeeder(MenuRepository menu, SettingsRepository settings)
    {
        _menu = menu;
        _settings = settings;
    }

    public void EnsureSeeded()
    {
        if (_menu.CountItems() > 0) return;
        ImportEmbedded();
    }

    public (int Categories, int Items) ImportEmbedded()
    {
        var categories = LoadEmbeddedCategories();
        var items = LoadEmbeddedItems();
        _menu.ReplaceAll(categories, items);

        var settings = _settings.Load();
        var shop = LoadEmbeddedRestaurant();
        if (shop is not null)
        {
            if (!string.IsNullOrWhiteSpace(shop.Name)) settings.ShopName = shop.Name;
            if (!string.IsNullOrWhiteSpace(shop.Address)) settings.ShopAddress = shop.Address;
            if (!string.IsNullOrWhiteSpace(shop.Postcode)) settings.ShopPostcode = shop.Postcode;
            if (!string.IsNullOrWhiteSpace(shop.Phone)) settings.ShopPhone = shop.Phone;
        }

        settings.LastMenuImportAt = DateTimeOffset.Now.ToString("o");
        _settings.Save(settings);
        return (categories.Count, items.Count);
    }

    private static List<Category> LoadEmbeddedCategories()
    {
        var json = ReadResource("categories.json");
        var rows = JsonSerializer.Deserialize<List<SeedCategory>>(json, SeedOptions) ?? [];
        return rows.Select(r => new Category
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            SortOrder = r.SortOrder,
            IsVisible = r.IsVisible,
        }).ToList();
    }

    private static List<MenuItem> LoadEmbeddedItems()
    {
        var json = ReadResource("menu_items.json");
        var rows = JsonSerializer.Deserialize<List<SeedMenuItem>>(json, SeedOptions) ?? [];
        return rows.Select(MapItem).ToList();
    }

    private static SeedRestaurant? LoadEmbeddedRestaurant()
    {
        var json = ReadResource("restaurant.json");
        return JsonSerializer.Deserialize<SeedRestaurant>(json, SeedOptions);
    }

    private static MenuItem MapItem(SeedMenuItem r)
    {
        var groups = (r.OptionGroups ?? []).Select(g => new OptionGroup
        {
            Id = g.Id,
            Name = g.Name,
            Type = ParseGroupType(g.Type),
            Required = g.Required,
            MinSelections = g.MinSelections,
            MaxSelections = g.MaxSelections,
            SortOrder = g.SortOrder,
            ShowWhen = g.ShowWhen is null
                ? null
                : new OptionShowWhen
                {
                    GroupId = g.ShowWhen.GroupId,
                    ChoiceIds = g.ShowWhen.ChoiceIds ?? [],
                },
            Choices = (g.Choices ?? []).Select(c => new OptionChoice
            {
                Id = c.Id,
                Label = c.Label,
                OptionTranslation = c.OptionTranslation,
                PriceDelta = c.PriceDelta,
                IsDefault = c.IsDefault,
                IsAvailable = c.IsAvailable ?? true,
            }).ToList(),
        }).ToList();

        return new MenuItem
        {
            Id = r.Id,
            CategoryId = r.CategoryId,
            MenuNumber = r.MenuNumber,
            Name = r.Name,
            ItemTranslation = r.ItemTranslation,
            Description = r.Description,
            BasePrice = r.BasePrice,
            IsAvailable = r.IsAvailable,
            IsBundle = r.IsBundle,
            OptionGroups = groups,
            SortOrder = r.SortOrder,
        };
    }

    private static OptionGroupType ParseGroupType(string? type) =>
        (type ?? "radio").ToLowerInvariant() switch
        {
            "checkbox" => OptionGroupType.Checkbox,
            "select" => OptionGroupType.Select,
            _ => OptionGroupType.Radio,
        };

    private static string ReadResource(string fileName)
    {
        var asm = typeof(MenuSeeder).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource not found: {fileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions SeedOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class SeedCategory
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
        [JsonPropertyName("is_visible")] public bool IsVisible { get; set; } = true;
    }

    private sealed class SeedRestaurant
    {
        public string? Name { get; set; }
        public string? Address { get; set; }
        public string? Postcode { get; set; }
        public string? Phone { get; set; }
    }

    private sealed class SeedMenuItem
    {
        public string Id { get; set; } = "";
        [JsonPropertyName("category_id")] public string CategoryId { get; set; } = "";
        [JsonPropertyName("menu_number")] public string? MenuNumber { get; set; }
        public string Name { get; set; } = "";
        [JsonPropertyName("item_translation")] public string? ItemTranslation { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("base_price")] public decimal BasePrice { get; set; }
        [JsonPropertyName("is_available")] public bool IsAvailable { get; set; } = true;
        [JsonPropertyName("is_bundle")] public bool IsBundle { get; set; }
        [JsonPropertyName("option_groups")] public List<SeedOptionGroup>? OptionGroups { get; set; }
        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    }

    private sealed class SeedOptionGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "radio";
        public bool Required { get; set; }
        public int? MinSelections { get; set; }
        public int? MaxSelections { get; set; }
        public int SortOrder { get; set; }
        public SeedShowWhen? ShowWhen { get; set; }
        public List<SeedChoice>? Choices { get; set; }
    }

    private sealed class SeedShowWhen
    {
        public string GroupId { get; set; } = "";
        public List<string>? ChoiceIds { get; set; }
    }

    private sealed class SeedChoice
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string? OptionTranslation { get; set; }
        public decimal PriceDelta { get; set; }
        public bool IsDefault { get; set; }
        public bool? IsAvailable { get; set; }
    }
}
