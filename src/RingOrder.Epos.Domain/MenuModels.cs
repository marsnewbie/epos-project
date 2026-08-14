namespace RingOrder.Epos.Domain;

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;
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

public sealed class OptionGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public OptionGroupType Type { get; set; } = OptionGroupType.Radio;
    public bool Required { get; set; }
    public int? MinSelections { get; set; }
    public int? MaxSelections { get; set; }
    public OptionShowWhen? ShowWhen { get; set; }
    public List<OptionChoice> Choices { get; set; } = [];
    public int SortOrder { get; set; }
}

public sealed class MenuItem
{
    public string Id { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string? MenuNumber { get; set; }
    public string Name { get; set; } = "";
    public string? ItemTranslation { get; set; }
    public string? Description { get; set; }
    public decimal BasePrice { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsBundle { get; set; }
    public List<OptionGroup> OptionGroups { get; set; } = [];
    public int SortOrder { get; set; }
}
