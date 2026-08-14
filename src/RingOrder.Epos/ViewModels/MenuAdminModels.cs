using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.ViewModels;

/// <summary>Editable menu catalogue models for Settings → Menu (full CRUD).</summary>
public partial class CategoryAdminRow : ObservableObject
{
    public CategoryAdminRow(Category category)
    {
        Category = category;
        Name = category.Name;
        IsVisible = category.IsVisible;
        SortOrderText = category.SortOrder.ToString();
    }

    public Category Category { get; }
    public string Id => Category.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _sortOrderText;

    public string VisibilityText => IsVisible ? "Shown" : "Hidden";
    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(VisibilityText));

    public override string ToString() => Name;

    public void ApplyToDomain()
    {
        Category.Name = Name.Trim();
        Category.IsVisible = IsVisible;
        Category.SortOrder = int.TryParse(SortOrderText, out var s) ? s : Category.SortOrder;
    }
}

public partial class MenuEditRow : ObservableObject
{
    public MenuEditRow(MenuItem item)
    {
        Item = item;
        RefreshFromItem();
    }

    public MenuItem Item { get; private set; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _priceText = "0.00";
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _optionsBadge = "";
    [ObservableProperty] private string _statusText = "On";

    public void RefreshFromItem()
    {
        Label = string.IsNullOrWhiteSpace(Item.MenuNumber) ? Item.Name : $"{Item.MenuNumber}  {Item.Name}";
        PriceText = Item.BasePrice.ToString("0.00");
        IsAvailable = Item.IsAvailable;
        OptionsBadge = Item.OptionGroups.Count == 0 ? "plain" : $"{Item.OptionGroups.Count} groups";
        StatusText = Item.IsAvailable ? "On sale" : "86";
    }
}

/// <summary>Full dish editor (right pane).</summary>
public partial class DishEditorVm : ObservableObject
{
    private string _baselineJson = "";

    public DishEditorVm(MenuItem item, IReadOnlyList<CategoryAdminRow> categories)
    {
        ItemId = item.Id;
        IsNew = false;
        MenuNumber = item.MenuNumber ?? "";
        Name = item.Name;
        KitchenName = item.ItemTranslation ?? "";
        Description = item.Description ?? "";
        PriceText = item.BasePrice.ToString("0.00");
        IsAvailable = item.IsAvailable;
        SortOrderText = item.SortOrder.ToString();
        CategoryId = item.CategoryId;
        Categories.Clear();
        foreach (var c in categories) Categories.Add(c);
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == item.CategoryId) ?? Categories.FirstOrDefault();
        foreach (var g in item.OptionGroups.OrderBy(x => x.SortOrder))
            Groups.Add(OptionGroupEditorVm.FromDomain(g));
        RefreshShowWhenTargets();
        CaptureBaseline();
    }

    public static DishEditorVm CreateNew(string categoryId, IReadOnlyList<CategoryAdminRow> categories)
    {
        var item = new MenuItem
        {
            Id = "item-" + Guid.NewGuid().ToString("N")[..10],
            CategoryId = categoryId,
            Name = "New dish",
            BasePrice = 0,
            IsAvailable = true,
            SortOrder = 99,
            OptionGroups = [],
        };
        var vm = new DishEditorVm(item, categories) { IsNew = true };
        vm.CaptureBaseline();
        return vm;
    }

    public string ItemId { get; }
    public bool IsNew { get; set; }
    public bool IsDirty => IsNew || !string.Equals(SnapshotJson(), _baselineJson, StringComparison.Ordinal);

    public void CaptureBaseline() => _baselineJson = SnapshotJson();

    private string SnapshotJson()
    {
        try
        {
            var d = ToDomain();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                d.CategoryId,
                d.MenuNumber,
                d.Name,
                d.ItemTranslation,
                d.Description,
                d.BasePrice,
                d.IsAvailable,
                d.SortOrder,
                Groups = d.OptionGroups.Select(g => new
                {
                    g.Id,
                    g.Name,
                    Type = g.Type.ToString(),
                    g.Required,
                    g.MinSelections,
                    g.MaxSelections,
                    ShowWhen = g.ShowWhen is null ? null : new { g.ShowWhen.GroupId, ChoiceIds = g.ShowWhen.ChoiceIds },
                    Choices = g.Choices.Select(c => new
                    {
                        c.Id,
                        c.Label,
                        c.OptionTranslation,
                        c.PriceDelta,
                        c.IsDefault,
                        c.IsAvailable,
                    }),
                }),
            });
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }

    public ObservableCollection<CategoryAdminRow> Categories { get; } = [];
    public ObservableCollection<OptionGroupEditorVm> Groups { get; } = [];

    [ObservableProperty] private string _menuNumber = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kitchenName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _priceText = "0.00";
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _sortOrderText = "0";
    [ObservableProperty] private string _categoryId = "";
    [ObservableProperty] private CategoryAdminRow? _selectedCategory;
    [ObservableProperty] private OptionGroupEditorVm? _selectedGroup;
    [ObservableProperty] private string _validationMessage = "";

    partial void OnSelectedCategoryChanged(CategoryAdminRow? value)
    {
        if (value is not null) CategoryId = value.Id;
    }

    public void RefreshShowWhenTargets()
    {
        foreach (var g in Groups)
        {
            g.ParentGroupOptions.Clear();
            g.ParentGroupOptions.Add(new ShowWhenOption("", "(none — always visible)"));
            foreach (var other in Groups.Where(x => x.GroupId != g.GroupId))
            {
                foreach (var ch in other.Choices)
                    g.ParentGroupOptions.Add(new ShowWhenOption(other.GroupId, ch.ChoiceId, $"{other.Name} → {ch.Label}"));
            }
            // restore selection
            if (g.ShowWhenGroupId is { Length: > 0 } && g.ShowWhenChoiceId is { Length: > 0 })
            {
                g.SelectedShowWhen = g.ParentGroupOptions.FirstOrDefault(o =>
                    o.GroupId == g.ShowWhenGroupId && o.ChoiceId == g.ShowWhenChoiceId)
                    ?? g.ParentGroupOptions.FirstOrDefault();
            }
            else
            {
                g.SelectedShowWhen = g.ParentGroupOptions.FirstOrDefault();
            }
        }
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Dish name is required.";
        if (!decimal.TryParse(PriceText, out var p) || p < 0) return "Price must be a valid number ≥ 0.";
        if (string.IsNullOrWhiteSpace(CategoryId)) return "Category is required.";
        foreach (var g in Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) return "Option group name required.";
            if (g.IsMulti)
            {
                var min = int.TryParse(g.MinText, out var mn) ? mn : 0;
                var max = int.TryParse(g.MaxText, out var mx) ? mx : g.Choices.Count;
                if (min < 0 || max < 0) return $"{g.Name}: min/max invalid.";
                if (min > max) return $"{g.Name}: min cannot exceed max.";
                if (max > g.Choices.Count && g.Choices.Count > 0)
                    return $"{g.Name}: max ({max}) > number of choices ({g.Choices.Count}).";
            }
            if (g.Choices.Count == 0) return $"{g.Name}: add at least one choice.";
            foreach (var c in g.Choices)
            {
                if (string.IsNullOrWhiteSpace(c.Label)) return $"{g.Name}: choice label required.";
                if (!string.IsNullOrWhiteSpace(c.PriceDeltaText) && !decimal.TryParse(c.PriceDeltaText, out _))
                    return $"{g.Name} / {c.Label}: price delta invalid.";
            }
        }
        return null;
    }

    public MenuItem ToDomain()
    {
        var groups = new List<OptionGroup>();
        var sort = 1;
        foreach (var g in Groups)
        {
            g.SyncShowWhenFromSelection();
            groups.Add(g.ToDomain(sort++));
        }

        return new MenuItem
        {
            Id = ItemId,
            CategoryId = SelectedCategory?.Id ?? CategoryId,
            MenuNumber = string.IsNullOrWhiteSpace(MenuNumber) ? null : MenuNumber.Trim(),
            Name = Name.Trim(),
            ItemTranslation = string.IsNullOrWhiteSpace(KitchenName) ? null : KitchenName.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            BasePrice = decimal.TryParse(PriceText, out var p) ? p : 0,
            IsAvailable = IsAvailable,
            SortOrder = int.TryParse(SortOrderText, out var s) ? s : 0,
            OptionGroups = groups,
        };
    }
}

public partial class OptionGroupEditorVm : ObservableObject
{
    public ObservableCollection<OptionChoiceEditorVm> Choices { get; } = [];
    public ObservableCollection<ShowWhenOption> ParentGroupOptions { get; } = [];

    public string GroupId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [ObservableProperty] private string _name = "Options";
    [ObservableProperty] private string _typeKey = "radio"; // radio | checkbox | select
    [ObservableProperty] private bool _required;
    [ObservableProperty] private string _minText = "0";
    [ObservableProperty] private string _maxText = "1";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private ShowWhenOption? _selectedShowWhen;
    [ObservableProperty] private string? _showWhenGroupId;
    [ObservableProperty] private string? _showWhenChoiceId;

    public bool IsMulti => TypeKey == "checkbox";
    public string TypeLabel => TypeKey switch
    {
        "checkbox" => "Multi (checkbox)",
        "select" => "Select (single)",
        _ => "Single (radio)",
    };

    public static readonly OptionTypeChoice[] TypeChoices =
    [
        new("radio", "Single · radio (pick one)"),
        new("checkbox", "Multi · checkbox (min/max)"),
        new("select", "Single · select (pick one)"),
    ];

    [ObservableProperty] private OptionTypeChoice? _selectedType;

    partial void OnSelectedTypeChanged(OptionTypeChoice? value)
    {
        if (value is not null && TypeKey != value.Key)
            TypeKey = value.Key;
    }

    partial void OnTypeKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(TypeLabel));
        SelectedType = TypeChoices.FirstOrDefault(t => t.Key == value) ?? TypeChoices[0];
        if (value == "checkbox" && MaxText == "1") MaxText = "3";
        if (value is "radio" or "select")
        {
            MinText = Required ? "1" : "0";
            MaxText = "1";
        }
    }

    partial void OnRequiredChanged(bool value)
    {
        if (TypeKey is "radio" or "select")
            MinText = value ? "1" : "0";
    }

    public static OptionGroupEditorVm FromDomain(OptionGroup g)
    {
        var typeKey = g.Type switch
        {
            OptionGroupType.Multi => "checkbox",
            OptionGroupType.Single => "select",
            _ => "radio",
        };
        var vm = new OptionGroupEditorVm
        {
            GroupId = g.Id,
            Name = g.Name,
            TypeKey = typeKey,
            SelectedType = TypeChoices.First(t => t.Key == typeKey),
            Required = g.Required,
            MinText = (g.MinSelections ?? (g.Required ? 1 : 0)).ToString(),
            MaxText = (g.MaxSelections ?? (g.Type == OptionGroupType.Multi ? g.Choices.Count : 1)).ToString(),
            ShowWhenGroupId = g.ShowWhen?.GroupId,
            ShowWhenChoiceId = g.ShowWhen?.ChoiceIds.FirstOrDefault(),
        };
        foreach (var c in g.Choices)
            vm.Choices.Add(OptionChoiceEditorVm.FromDomain(c));
        return vm;
    }

    public void SyncShowWhenFromSelection()
    {
        if (SelectedShowWhen is null || string.IsNullOrEmpty(SelectedShowWhen.GroupId))
        {
            ShowWhenGroupId = null;
            ShowWhenChoiceId = null;
        }
        else
        {
            ShowWhenGroupId = SelectedShowWhen.GroupId;
            ShowWhenChoiceId = SelectedShowWhen.ChoiceId;
        }
    }

    public OptionGroup ToDomain(int sortOrder)
    {
        SyncShowWhenFromSelection();
        var type = TypeKey switch
        {
            "checkbox" => OptionGroupType.Multi,
            "select" => OptionGroupType.Single,
            _ => OptionGroupType.Single,
        };
        var g = new OptionGroup
        {
            Id = GroupId,
            Name = Name.Trim(),
            Type = type,
            Required = Required,
            SortOrder = sortOrder,
            MinSelections = type == OptionGroupType.Multi
                ? (int.TryParse(MinText, out var mn) ? mn : 0)
                : null,
            MaxSelections = type == OptionGroupType.Multi
                ? (int.TryParse(MaxText, out var mx) ? mx : Choices.Count)
                : null,
            ShowWhen = string.IsNullOrWhiteSpace(ShowWhenGroupId) || string.IsNullOrWhiteSpace(ShowWhenChoiceId)
                ? null
                : new OptionShowWhen
                {
                    GroupId = ShowWhenGroupId!,
                    ChoiceIds = [ShowWhenChoiceId!],
                },
            Choices = Choices.Select(c => c.ToDomain()).ToList(),
        };
        return g;
    }
}

public partial class OptionChoiceEditorVm : ObservableObject
{
    public string ChoiceId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _kitchenName = "";
    [ObservableProperty] private string _priceDeltaText = "0";
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isAvailable = true;

    public static OptionChoiceEditorVm FromDomain(OptionChoice c) => new()
    {
        ChoiceId = c.Id,
        Label = c.Label,
        KitchenName = c.OptionTranslation ?? "",
        PriceDeltaText = c.PriceDelta.ToString("0.##"),
        IsDefault = c.IsDefault,
        IsAvailable = c.IsAvailable,
    };

    public OptionChoice ToDomain() => new()
    {
        Id = ChoiceId,
        Label = Label.Trim(),
        OptionTranslation = string.IsNullOrWhiteSpace(KitchenName) ? null : KitchenName.Trim(),
        PriceDelta = decimal.TryParse(PriceDeltaText, out var d) ? d : 0,
        IsDefault = IsDefault,
        IsAvailable = IsAvailable,
    };
}

public sealed class OptionTypeChoice(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public sealed class ShowWhenOption
{
    public ShowWhenOption(string groupId, string label)
    {
        GroupId = groupId;
        ChoiceId = "";
        Label = label;
    }

    public ShowWhenOption(string groupId, string choiceId, string label)
    {
        GroupId = groupId;
        ChoiceId = choiceId;
        Label = label;
    }

    public string GroupId { get; }
    public string ChoiceId { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class QuickNoteEditRow : ObservableObject
{
    public QuickNoteEditRow(string en, string zh)
    {
        En = en;
        Zh = zh;
    }

    [ObservableProperty] private string _en;
    [ObservableProperty] private string _zh;
}

public sealed class OptionGroupAdminRow
{
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string ChoicesText { get; set; } = "";
}
