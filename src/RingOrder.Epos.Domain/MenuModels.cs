namespace RingOrder.Epos.Domain;

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Kitchen-language name. The counter always shows <see cref="Name"/>.</summary>
    public string? Translation { get; set; }

    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;

    /// <summary>Station that cooks this category by default; items may override.</summary>
    public string PrintClass { get; set; } = Domain.PrintClass.Kitchen;

    /// <summary>Tax class inherited by items in this category unless overridden.</summary>
    public string TaxClassId { get; set; } = "hot-food";

    public override string ToString() => Name;
}

public sealed class OptionChoice
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? OptionTranslation { get; set; }
    public decimal PriceDelta { get; set; }
    public bool IsDefault { get; set; }
    public bool IsAvailable { get; set; } = true;
}

public sealed class OptionShowWhen
{
    public string GroupId { get; set; } = "";
    public List<string> ChoiceIds { get; set; } = [];
}

/// <summary>
/// A modifier group in the shop's shared catalogue. Several dishes reference the
/// same group, so "spice level" is edited once rather than fifty times.
/// <para>
/// <see cref="SortOrder"/> and <see cref="ShowWhen"/> are not properties of the
/// group itself — they come from the dish that references it, and are filled in
/// when a dish is loaded. Two dishes may show the same group in a different
/// position, or one may reveal it conditionally while another always shows it.
/// </para>
/// </summary>
public sealed class OptionGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Translation { get; set; }
    public OptionGroupType Type { get; set; } = OptionGroupType.Single;
    public bool Required { get; set; }
    public int? MinSelections { get; set; }
    public int? MaxSelections { get; set; }
    public List<OptionChoice> Choices { get; set; } = [];

    /// <summary>Position on the dish currently being served. Set from the link.</summary>
    public int SortOrder { get; set; }

    /// <summary>Reveal condition on the dish currently being served. Set from the link.</summary>
    public OptionShowWhen? ShowWhen { get; set; }

    /// <summary>Copy carrying one dish's placement, so shared state is never mutated.</summary>
    public OptionGroup ForItem(int sortOrder, OptionShowWhen? showWhen) => new()
    {
        Id = Id,
        Name = Name,
        Translation = Translation,
        Type = Type,
        Required = Required,
        MinSelections = MinSelections,
        MaxSelections = MaxSelections,
        Choices = Choices,
        SortOrder = sortOrder,
        ShowWhen = showWhen,
    };
}

/// <summary>A dish's reference to a shared option group.</summary>
public sealed class MenuItemOptionLink
{
    public string GroupId { get; set; } = "";
    public int SortOrder { get; set; }
    public OptionShowWhen? ShowWhen { get; set; }
}

public sealed class MenuItem
{
    public string Id { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string? MenuNumber { get; set; }
    public string Name { get; set; } = "";
    public string? ItemTranslation { get; set; }
    public string? Description { get; set; }

    /// <summary>Price on the default tier.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>
    /// Overrides per price tier (eat-in, marketplace). Absent means the tier uses
    /// <see cref="BasePrice"/> — most dishes never need an entry here.
    /// </summary>
    public Dictionary<string, decimal> TierPrices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Station that cooks it; falls back to the category's class.</summary>
    public string? PrintClass { get; set; }

    /// <summary>Tax class; falls back to the category's class.</summary>
    public string? TaxClassId { get; set; }

    public bool IsAvailable { get; set; } = true;
    public bool IsBundle { get; set; }
    public int SortOrder { get; set; }

    /// <summary>References into the shared catalogue, in this dish's order.</summary>
    public List<MenuItemOptionLink> OptionLinks { get; set; } = [];

    /// <summary>
    /// Groups resolved for this dish, populated on load. Empty until the
    /// repository has attached the shared catalogue.
    /// </summary>
    public List<OptionGroup> OptionGroups { get; set; } = [];

    public decimal PriceForTier(string? tierId) =>
        tierId is not null && TierPrices.TryGetValue(tierId, out var price) ? price : BasePrice;
}

/// <summary>A VAT band. Rates are basis points so 20% is 2000 and never 0.199999.</summary>
public sealed class TaxClass
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int RateBasisPoints { get; set; }

    public decimal Rate => RateBasisPoints / 10_000m;
}

/// <summary>
/// A named price list. UK takeaways routinely charge differently for eat-in
/// (VAT) and for marketplace orders (commission), and a shop that cannot express
/// that asks for a second copy of its menu.
/// </summary>
public sealed class PriceTier
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}
