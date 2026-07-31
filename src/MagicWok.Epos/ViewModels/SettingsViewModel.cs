using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicWok.Epos.Domain;
using MagicWok.Epos.Services;

namespace MagicWok.Epos.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action? _onSaved;

    public SettingsViewModel(AppServices app, Action<string> setStatus, Action? onSaved = null)
    {
        _app = app;
        _setStatus = setStatus;
        _onSaved = onSaved;
        RefreshUiLabels();
        Reload();
    }

    public ObservableCollection<MenuEditRow> MenuRows { get; } = [];
    public ObservableCollection<QuickNoteEditRow> NoteRows { get; } = [];
    public ObservableCollection<CategoryAdminRow> MenuCategories { get; } = [];
    public OptionTypeChoice[] OptionTypeChoices { get; } = OptionGroupEditorVm.TypeChoices;

    [ObservableProperty] private CategoryAdminRow? _selectedMenuCategory;
    [ObservableProperty] private MenuEditRow? _selectedMenuItem;
    [ObservableProperty] private DishEditorVm? _dishEditor;
    [ObservableProperty] private bool _hasDishEditor;
    [ObservableProperty] private bool _editingCategory;
    [ObservableProperty] private bool _showEmptyEditorHint = true;
    [ObservableProperty] private string _categoryEditName = "";
    [ObservableProperty] private string _categoryEditSort = "0";
    [ObservableProperty] private bool _categoryEditVisible = true;

    [ObservableProperty] private string _section = "Shop";
    [ObservableProperty] private bool _isShop = true;
    [ObservableProperty] private bool _isMenu;
    [ObservableProperty] private bool _isNotes;
    [ObservableProperty] private bool _isDelivery;
    [ObservableProperty] private bool _isHardware;
    [ObservableProperty] private bool _isStaff;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isShift;

    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _shopAddress = "";
    [ObservableProperty] private string _shopPostcode = "";
    [ObservableProperty] private string _shopPhone = "";
    [ObservableProperty] private string _kitchenPrinter = "GlPrinter80";
    [ObservableProperty] private string _frontPrinter = "GlPrinter80";
    [ObservableProperty] private string _printEncoding = "gbk";
    [ObservableProperty] private bool _printChineseAsRaster = true;
    [ObservableProperty] private bool _openDrawerOnCash = true;
    [ObservableProperty] private bool _sendKitchenOnPay = true;
    [ObservableProperty] private bool _printFrontOnPay = true;
    [ObservableProperty] private bool _autoKitchenPrintOnline = true;
    [ObservableProperty] private bool _printVoidKitchenTicket = true;
    [ObservableProperty] private string _onlineBaseUrl = "";
    [ObservableProperty] private string _orderServerUrl = "";
    [ObservableProperty] private string _callbackUrl = "";
    [ObservableProperty] private string _printedUrl = "";
    [ObservableProperty] private string _onlineResId = "";
    [ObservableProperty] private string _onlineUsername = "";
    [ObservableProperty] private string _onlinePassword = "";
    [ObservableProperty] private string _pollIntervalText = "4";
    [ObservableProperty] private bool _onlinePollingEnabled;
    [ObservableProperty] private string _defaultDeliveryFeeText = "0";
    [ObservableProperty] private string _menuInfo = "";
    [ObservableProperty] private string _menuSearch = "";
    [ObservableProperty] private string _managerPin = "1234";
    [ObservableProperty] private string _cashierPin = "";
    [ObservableProperty] private string _callerIdMode = "simulate";
    [ObservableProperty] private string _callerIdCom = "COM3";
    [ObservableProperty] private bool _callerIdEnabled;
    [ObservableProperty] private string _shiftSummary = "";
    [ObservableProperty] private string _newNoteEn = "";
    [ObservableProperty] private string _newNoteZh = "";

    [ObservableProperty] private string _lblSecShop = "Shop";
    [ObservableProperty] private string _lblSecMenu = "Menu";
    [ObservableProperty] private string _lblSecNotes = "Quick notes";
    [ObservableProperty] private string _lblSecDelivery = "Delivery";
    [ObservableProperty] private string _lblSecHardware = "Hardware";
    [ObservableProperty] private string _lblSecStaff = "Staff / PIN";
    [ObservableProperty] private string _lblSecShift = "Shift today";
    [ObservableProperty] private string _lblSecOnline = "Online";
    [ObservableProperty] private string _lblSave = "Save";
    [ObservableProperty] private string _lblAddCategory = "+ Category";
    [ObservableProperty] private string _lblEdit = "Edit";
    [ObservableProperty] private string _lblHideShow = "Hide/Show";
    [ObservableProperty] private string _lblDelete = "Delete";
    [ObservableProperty] private string _lblAddDish = "+ Dish";
    [ObservableProperty] private string _lblSaveDish = "Save dish";
    [ObservableProperty] private string _lblDuplicate = "Duplicate";
    [ObservableProperty] private string _lbl86 = "86";
    [ObservableProperty] private string _lblAddGroup = "+ Group";
    [ObservableProperty] private string _lblAddChoice = "+ Choice";
    [ObservableProperty] private string _lblRemove = "Remove";
    [ObservableProperty] private string _lblReimport = "Re-import seed";
    [ObservableProperty] private string _lblMenuOpsTitle = "Menu operations";
    [ObservableProperty] private string _lblMenuOpsHint = "";
    [ObservableProperty] private string _lblCategories = "Categories";
    [ObservableProperty] private string _lblDishes = "Dishes";
    [ObservableProperty] private string _lblDishEditor = "Dish editor";
    [ObservableProperty] private string _lblOptionGroups = "Option groups";
    [ObservableProperty] private string _lblChoices = "Choices";
    [ObservableProperty] private string _lblUiLangNote = "";
    [ObservableProperty] private string _lblRequired = "Req";

    public void RefreshUiLabels()
    {
        LblSecShop = UiText.SecShop;
        LblSecMenu = UiText.SecMenu;
        LblSecNotes = UiText.SecNotes;
        LblSecDelivery = UiText.SecDelivery;
        LblSecHardware = UiText.SecHardware;
        LblSecStaff = UiText.SecStaff;
        LblSecShift = UiText.SecShift;
        LblSecOnline = UiText.SecOnline;
        LblSave = UiText.SaveSettings;
        LblAddCategory = UiText.AddCategory;
        LblEdit = UiText.Edit;
        LblHideShow = UiText.HideShow;
        LblDelete = UiText.Delete;
        LblAddDish = UiText.AddDish;
        LblSaveDish = UiText.SaveDish;
        LblDuplicate = UiText.Duplicate;
        Lbl86 = UiText.EightySix;
        LblAddGroup = UiText.AddGroup;
        LblAddChoice = UiText.AddChoice;
        LblRemove = UiText.Remove;
        LblReimport = UiText.Reimport;
        LblMenuOpsTitle = UiText.MenuOpsTitle;
        LblMenuOpsHint = UiText.MenuOpsHint;
        LblCategories = UiText.Categories;
        LblDishes = UiText.Dishes;
        LblDishEditor = UiText.DishEditor;
        LblOptionGroups = UiText.OptionGroups;
        LblChoices = UiText.Choices;
        LblUiLangNote = UiText.UiLangNote;
        LblRequired = UiText.Pick("Req", "必选");
    }

    [RelayCommand]
    private void GoSection(string? section)
    {
        Section = section ?? "Shop";
        IsShop = Section == "Shop";
        IsMenu = Section == "Menu";
        IsNotes = Section == "Notes";
        IsDelivery = Section == "Delivery";
        IsHardware = Section == "Hardware";
        IsStaff = Section == "Staff";
        IsOnline = Section == "Online";
        IsShift = Section == "Shift";
        if (IsMenu) ReloadMenuBrowser();
        if (IsNotes) ReloadNoteRows();
        if (IsShift) ReloadShift();
    }

    public void Reload()
    {
        var s = _app.ReloadSettings();
        ShopName = s.ShopName;
        ShopAddress = s.ShopAddress;
        ShopPostcode = s.ShopPostcode;
        ShopPhone = s.ShopPhone;
        KitchenPrinter = s.KitchenPrinterName;
        FrontPrinter = s.FrontPrinterName;
        PrintEncoding = s.PrintEncoding;
        PrintChineseAsRaster = s.PrintChineseAsRaster;
        OpenDrawerOnCash = s.OpenDrawerOnCash;
        SendKitchenOnPay = s.SendKitchenOnPay;
        PrintFrontOnPay = s.PrintFrontOnPay;
        AutoKitchenPrintOnline = s.AutoKitchenPrintOnline;
        PrintVoidKitchenTicket = s.PrintVoidKitchenTicket;
        OnlineBaseUrl = s.OnlineBaseUrl;
        OrderServerUrl = s.OnlineOrderServerUrl;
        CallbackUrl = s.OnlineCallbackUrl;
        PrintedUrl = s.OnlinePrintedUrl;
        OnlineResId = s.OnlineResId;
        OnlineUsername = s.OnlineUsername;
        OnlinePassword = s.OnlinePassword;
        PollIntervalText = s.OnlinePollIntervalSeconds.ToString();
        OnlinePollingEnabled = s.OnlinePollingEnabled;
        DefaultDeliveryFeeText = s.DefaultDeliveryFee.ToString("0.##");
        ManagerPin = s.ManagerPin;
        CashierPin = s.CashierPin ?? "";
        CallerIdEnabled = s.CallerIdEnabled;
        CallerIdMode = s.CallerIdMode;
        CallerIdCom = s.CallerIdComPort;
        MenuInfo = UiText.Pick(
            $"Items: {_app.Menu.CountItems()} | Last import: {s.LastMenuImportAt ?? "n/a"}",
            $"菜品: {_app.Menu.CountItems()} | 上次导入: {s.LastMenuImportAt ?? "无"}");
        ReloadMenuBrowser();
        ReloadNoteRows();
        ReloadShift();
        GoSection(Section);
    }

    private void ReloadMenuBrowser()
    {
        var prevCat = SelectedMenuCategory?.Id;
        var prevItem = SelectedMenuItem?.Item.Id;
        var editingId = DishEditor?.ItemId;
        MenuCategories.Clear();
        foreach (var c in _app.Menu.GetCategories(visibleOnly: false))
            MenuCategories.Add(new CategoryAdminRow(c));

        SelectedMenuCategory = MenuCategories.FirstOrDefault(c => c.Id == prevCat)
                               ?? MenuCategories.FirstOrDefault();
        ReloadMenuRows(preferItemId: editingId ?? prevItem);
        MenuInfo = $"Categories: {MenuCategories.Count} · Dishes: {_app.Menu.CountItems()} · Last import: {_app.GetSettings().LastMenuImportAt ?? "n/a"}";
    }

    private void ReloadMenuRows(string? preferItemId = null)
    {
        MenuRows.Clear();
        IEnumerable<MenuItem> items;
        if (!string.IsNullOrWhiteSpace(MenuSearch))
        {
            var q = MenuSearch.Trim();
            items = _app.Menu.GetItems(availableOnly: false).Where(i =>
                (i.MenuNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (i.ItemTranslation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        else if (SelectedMenuCategory is not null)
        {
            items = _app.Menu.GetItems(SelectedMenuCategory.Id, availableOnly: false);
        }
        else
        {
            items = _app.Menu.GetItems(availableOnly: false);
        }

        foreach (var i in items.Take(400))
            MenuRows.Add(new MenuEditRow(i));

        var pick = preferItemId is not null
            ? MenuRows.FirstOrDefault(r => r.Item.Id == preferItemId) ?? MenuRows.FirstOrDefault()
            : MenuRows.FirstOrDefault();
        SelectedMenuItem = pick;
        if (pick is not null)
            OpenDishEditor(pick.Item);
        else
            ClearDishEditor();
    }

    partial void OnSelectedMenuCategoryChanged(CategoryAdminRow? value)
    {
        EditingCategory = false;
        if (value is not null)
        {
            CategoryEditName = value.Name;
            CategoryEditSort = value.SortOrderText;
            CategoryEditVisible = value.IsVisible;
        }
        if (!string.IsNullOrWhiteSpace(MenuSearch)) return;
        _ = ChangeCategorySelectionAsync(value);
    }

    private async Task ChangeCategorySelectionAsync(CategoryAdminRow? value)
    {
        if (!await ConfirmDiscardDishEditsAsync())
        {
            var keepCat = DishEditor is null
                ? null
                : MenuCategories.FirstOrDefault(c => c.Id == (_app.Menu.GetItem(DishEditor.ItemId)?.CategoryId ?? DishEditor.CategoryId));
            if (keepCat is not null && !ReferenceEquals(SelectedMenuCategory, keepCat))
                SelectedMenuCategory = keepCat;
            return;
        }
        ReloadMenuRows();
    }

    partial void OnSelectedMenuItemChanged(MenuEditRow? value)
    {
        if (value is null) return;
        if (DishEditor?.ItemId == value.Item.Id) return;
        _ = SwitchDishSelectionAsync(value);
    }

    private async Task SwitchDishSelectionAsync(MenuEditRow value)
    {
        if (!await ConfirmDiscardDishEditsAsync())
        {
            // Revert list selection to the dish still being edited
            var keepId = DishEditor?.ItemId;
            SelectedMenuItem = keepId is null
                ? null
                : MenuRows.FirstOrDefault(r => r.Item.Id == keepId);
            return;
        }
        OpenDishEditor(value.Item);
    }

    private async Task<bool> ConfirmDiscardDishEditsAsync()
    {
        if (DishEditor is null || !DishEditor.IsDirty) return true;
        return await UiPrompt.ConfirmAsync(
            "Unsaved dish changes",
            "Discard edits to this dish? Save dish first to keep option groups and prices.");
    }

    private void OpenDishEditor(MenuItem item)
    {
        EditingCategory = false;
        var fresh = _app.Menu.GetItem(item.Id) ?? item;
        DishEditor = new DishEditorVm(fresh, MenuCategories.ToList());
        HasDishEditor = true;
        ShowEmptyEditorHint = false;
    }

    private void ClearDishEditor()
    {
        DishEditor = null;
        HasDishEditor = false;
        ShowEmptyEditorHint = !EditingCategory;
    }

    private void ReloadNoteRows()
    {
        NoteRows.Clear();
        var notes = _app.GetSettings().QuickNotes;
        if (notes.Count == 0) notes = QuickKitchenNotes.CreateDefaultList();
        foreach (var n in notes)
            NoteRows.Add(new QuickNoteEditRow(n.En, n.Zh));
    }

    private void ReloadShift()
    {
        var all = _app.Orders.GetToday();
        var active = all.Where(o => o.Status is not (PosOrderStatus.Voided or PosOrderStatus.Cancelled)).ToList();
        var paidDone = active.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList();
        var cash = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.Cash).Sum(t => t.Amount);
        var card = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.CardManual).Sum(t => t.Amount);
        var online = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.OnlinePaid).Sum(t => t.Amount);
        var dueOpen = active.Where(o => o.IsUnpaid).Sum(o => o.BalanceDue);
        var voided = all.Count(o => o.Status == PosOrderStatus.Voided);
        ShiftSummary =
            $"Cash taken (incl. partial): £{cash:0.00}\n" +
            $"Card taken (incl. partial): £{card:0.00}\n" +
            $"Online paid: £{online:0.00}\n" +
            $"Open balance due: £{dueOpen:0.00}\n" +
            $"Paid-in-full tickets: {paidDone.Count}\n" +
            $"Gross of paid tickets: £{paidDone.Sum(o => o.Total):0.00}\n" +
            $"Voided: {voided}\n" +
            $"Unpaid (still due): {active.Count(o => o.IsUnpaid)}";
    }

    partial void OnMenuSearchChanged(string value) => ReloadMenuRows();

    // ── Category CRUD ──────────────────────────────────────────────

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = await UiPrompt.PromptTextAsync("New category", "Category name", initial: "New category");
        if (string.IsNullOrWhiteSpace(name)) return;
        var cat = new Category
        {
            Id = "cat-" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            IsVisible = true,
            SortOrder = MenuCategories.Count + 1,
        };
        _app.Menu.UpsertCategory(cat);
        ReloadMenuBrowser();
        SelectedMenuCategory = MenuCategories.FirstOrDefault(c => c.Id == cat.Id);
        _setStatus($"Created category {cat.Name}");
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private async Task BeginEditCategoryAsync()
    {
        if (SelectedMenuCategory is null) return;
        if (!await ConfirmDiscardDishEditsAsync()) return;
        EditingCategory = true;
        HasDishEditor = false;
        ShowEmptyEditorHint = false;
        DishEditor = null;
        CategoryEditName = SelectedMenuCategory.Name;
        CategoryEditSort = SelectedMenuCategory.SortOrderText;
        CategoryEditVisible = SelectedMenuCategory.IsVisible;
    }

    [RelayCommand]
    private void SaveCategory()
    {
        if (SelectedMenuCategory is null) return;
        if (string.IsNullOrWhiteSpace(CategoryEditName))
        {
            _setStatus("Category name required");
            return;
        }
        SelectedMenuCategory.Name = CategoryEditName.Trim();
        SelectedMenuCategory.SortOrderText = CategoryEditSort;
        SelectedMenuCategory.IsVisible = CategoryEditVisible;
        SelectedMenuCategory.ApplyToDomain();
        _app.Menu.UpsertCategory(SelectedMenuCategory.Category);
        EditingCategory = false;
        ReloadMenuBrowser();
        _setStatus($"Saved category {CategoryEditName}");
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private void ToggleCategoryVisible(CategoryAdminRow? row)
    {
        if (row is null) return;
        row.IsVisible = !row.IsVisible;
        row.Category.IsVisible = row.IsVisible;
        _app.Menu.SetCategoryVisible(row.Id, row.IsVisible);
        _setStatus(row.IsVisible ? $"Category shown: {row.Name}" : $"Category hidden: {row.Name}");
        ReloadMenuBrowser();
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync()
    {
        if (SelectedMenuCategory is null) return;
        var count = _app.Menu.CountItemsInCategory(SelectedMenuCategory.Id);
        if (count > 0)
        {
            _setStatus($"Cannot delete — {count} dishes still in this category. Move or delete dishes first.");
            return;
        }
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "Delete category"))
            return;
        if (!await UiPrompt.ConfirmAsync("Delete category?", $"Delete “{SelectedMenuCategory.Name}”? This cannot be undone."))
            return;
        _app.Menu.DeleteCategory(SelectedMenuCategory.Id);
        ClearDishEditor();
        ReloadMenuBrowser();
        _setStatus("Category deleted");
        _onSaved?.Invoke();
    }

    // ── Dish CRUD ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddDishAsync()
    {
        var catId = SelectedMenuCategory?.Id ?? MenuCategories.FirstOrDefault()?.Id;
        if (catId is null)
        {
            _setStatus("Create a category first");
            return;
        }
        if (!await ConfirmDiscardDishEditsAsync()) return;
        EditingCategory = false;
        DishEditor = DishEditorVm.CreateNew(catId, MenuCategories.ToList());
        HasDishEditor = true;
        ShowEmptyEditorHint = false;
        SelectedMenuItem = null;
        _setStatus("New dish — fill details, add option groups, then Save dish");
    }

    [RelayCommand]
    private async Task DuplicateDishAsync()
    {
        if (DishEditor is null) return;
        if (DishEditor.IsDirty)
        {
            var ok = await UiPrompt.ConfirmAsync(
                "Duplicate from current editor?",
                "Uses the values on screen (including unsaved). Continue?");
            if (!ok) return;
        }

        var src = DishEditor.ToDomain();
        var copy = new MenuItem
        {
            Id = "item-" + Guid.NewGuid().ToString("N")[..10],
            CategoryId = src.CategoryId,
            MenuNumber = null,
            Name = src.Name.EndsWith(" (copy)", StringComparison.Ordinal) ? src.Name : src.Name + " (copy)",
            ItemTranslation = src.ItemTranslation,
            Description = src.Description,
            BasePrice = src.BasePrice,
            IsAvailable = src.IsAvailable,
            SortOrder = src.SortOrder,
            OptionGroups = CloneGroupsRemapped(src.OptionGroups),
        };
        EditingCategory = false;
        DishEditor = new DishEditorVm(copy, MenuCategories.ToList()) { IsNew = true };
        DishEditor.CaptureBaseline();
        HasDishEditor = true;
        ShowEmptyEditorHint = false;
        SelectedMenuItem = null;
        _setStatus("Duplicated dish — set a new menu # then Save dish");
    }

    private static List<OptionGroup> CloneGroupsRemapped(IReadOnlyList<OptionGroup> source)
    {
        var groupMap = new Dictionary<string, string>();
        var choiceMap = new Dictionary<string, string>();
        foreach (var g in source)
        {
            groupMap[g.Id] = "og-" + Guid.NewGuid().ToString("N")[..10];
            foreach (var c in g.Choices)
                choiceMap[c.Id] = "oc-" + Guid.NewGuid().ToString("N")[..10];
        }

        return source.Select(g =>
        {
            OptionShowWhen? showWhen = null;
            if (g.ShowWhen is not null && groupMap.ContainsKey(g.ShowWhen.GroupId))
            {
                var ids = g.ShowWhen.ChoiceIds.Where(choiceMap.ContainsKey).Select(id => choiceMap[id]).ToList();
                if (ids.Count > 0)
                    showWhen = new OptionShowWhen { GroupId = groupMap[g.ShowWhen.GroupId], ChoiceIds = ids };
            }

            return new OptionGroup
            {
                Id = groupMap[g.Id],
                Name = g.Name,
                Type = g.Type,
                Required = g.Required,
                MinSelections = g.MinSelections,
                MaxSelections = g.MaxSelections,
                SortOrder = g.SortOrder,
                ShowWhen = showWhen,
                Choices = g.Choices.Select(c => new OptionChoice
                {
                    Id = choiceMap[c.Id],
                    Label = c.Label,
                    OptionTranslation = c.OptionTranslation,
                    PriceDelta = c.PriceDelta,
                    IsDefault = c.IsDefault,
                    IsAvailable = c.IsAvailable,
                }).ToList(),
            };
        }).ToList();
    }

    [RelayCommand]
    private void SaveDish()
    {
        if (DishEditor is null) return;
        var err = DishEditor.Validate();
        if (err is not null)
        {
            DishEditor.ValidationMessage = err;
            _setStatus(err);
            return;
        }

        // Unique menu number check
        var domain = DishEditor.ToDomain();
        if (!string.IsNullOrWhiteSpace(domain.MenuNumber))
        {
            var clash = _app.Menu.FindByMenuNumber(domain.MenuNumber!);
            if (clash is not null && clash.Id != domain.Id)
            {
                DishEditor.ValidationMessage = $"Menu number {domain.MenuNumber} already used by {clash.Name}";
                _setStatus(DishEditor.ValidationMessage);
                return;
            }
        }

        _app.Menu.UpsertItem(domain);
        DishEditor.ValidationMessage = "";
        var id = domain.Id;
        ReloadMenuBrowser();
        SelectedMenuItem = MenuRows.FirstOrDefault(r => r.Item.Id == id);
        if (SelectedMenuItem is not null)
            OpenDishEditor(SelectedMenuItem.Item);
        _setStatus($"Saved dish {domain.MenuNumber} {domain.Name}".Trim());
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteDishAsync()
    {
        if (DishEditor is null) return;
        if (DishEditor.IsNew)
        {
            ClearDishEditor();
            return;
        }
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "Delete dish"))
            return;
        if (!await UiPrompt.ConfirmAsync("Delete dish?", $"Delete “{DishEditor.Name}”? Orders history keeps past lines; Sell will no longer offer this dish."))
            return;
        _app.Menu.DeleteItem(DishEditor.ItemId);
        ClearDishEditor();
        ReloadMenuBrowser();
        _setStatus("Dish deleted");
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private void Toggle86Current()
    {
        if (DishEditor is null || DishEditor.IsNew) return;
        DishEditor.IsAvailable = !DishEditor.IsAvailable;
        _app.Menu.SetItemAvailable(DishEditor.ItemId, DishEditor.IsAvailable);
        _setStatus(DishEditor.IsAvailable ? "Back on sale" : "86 — sold out today");
        ReloadMenuRows(preferItemId: DishEditor.ItemId);
        _onSaved?.Invoke();
    }

    // ── Option group / choice CRUD (in-memory until Save dish) ─────

    [RelayCommand]
    private void AddOptionGroup()
    {
        if (DishEditor is null) return;
        var g = new OptionGroupEditorVm
        {
            Name = "New options",
            TypeKey = "radio",
            SelectedType = OptionGroupEditorVm.TypeChoices[0],
            Required = true,
            MinText = "1",
            MaxText = "1",
        };
        g.Choices.Add(new OptionChoiceEditorVm { Label = "Option A", PriceDeltaText = "0", IsDefault = true });
        g.Choices.Add(new OptionChoiceEditorVm { Label = "Option B", PriceDeltaText = "0" });
        DishEditor.Groups.Add(g);
        DishEditor.SelectedGroup = g;
        DishEditor.RefreshShowWhenTargets();
    }

    [RelayCommand]
    private void RemoveOptionGroup(OptionGroupEditorVm? group)
    {
        if (DishEditor is null || group is null) return;
        DishEditor.Groups.Remove(group);
        DishEditor.RefreshShowWhenTargets();
    }

    [RelayCommand]
    private void AddChoice(OptionGroupEditorVm? group)
    {
        if (group is null) return;
        group.Choices.Add(new OptionChoiceEditorVm { Label = "New choice", PriceDeltaText = "0" });
        DishEditor?.RefreshShowWhenTargets();
    }

    [RelayCommand]
    private void RemoveChoice(OptionChoiceEditorVm? choice)
    {
        if (DishEditor is null || choice is null) return;
        foreach (var g in DishEditor.Groups)
        {
            if (g.Choices.Remove(choice))
            {
                DishEditor.RefreshShowWhenTargets();
                return;
            }
        }
    }

    [RelayCommand]
    private void RefreshShowWhen()
    {
        DishEditor?.RefreshShowWhenTargets();
    }

    [RelayCommand]
    private void Save()
    {
        var s = _app.GetSettings();
        s.ShopName = ShopName.Trim();
        s.ShopAddress = ShopAddress.Trim();
        s.ShopPostcode = ShopPostcode.Trim();
        s.ShopPhone = ShopPhone.Trim();
        s.KitchenPrinterName = KitchenPrinter.Trim();
        s.FrontPrinterName = FrontPrinter.Trim();
        s.PrintEncoding = PrintEncoding.Trim();
        s.PrintChineseAsRaster = PrintChineseAsRaster;
        s.OpenDrawerOnCash = OpenDrawerOnCash;
        s.SendKitchenOnPay = SendKitchenOnPay;
        s.PrintFrontOnPay = PrintFrontOnPay;
        s.AutoKitchenPrintOnline = AutoKitchenPrintOnline;
        s.PrintVoidKitchenTicket = PrintVoidKitchenTicket;
        s.OnlineBaseUrl = OnlineBaseUrl.Trim().TrimEnd('/');
        s.OnlineOrderServerUrl = OrderServerUrl.Trim();
        s.OnlinePrintedUrl = PrintedUrl.Trim();
        s.OnlineCallbackUrl = string.IsNullOrWhiteSpace(PrintedUrl) ? CallbackUrl.Trim() : PrintedUrl.Trim();
        s.OnlineResId = OnlineResId.Trim();
        s.OnlineUsername = OnlineUsername.Trim();
        s.OnlinePassword = OnlinePassword;
        s.OnlinePollIntervalSeconds = int.TryParse(PollIntervalText, out var iv) ? Math.Clamp(iv, 2, 60) : 4;
        s.OnlinePollingEnabled = OnlinePollingEnabled;
        s.DefaultDeliveryFee = decimal.TryParse(DefaultDeliveryFeeText, out var fee) ? fee : 0;
        s.ManagerPin = string.IsNullOrWhiteSpace(ManagerPin) ? "1234" : ManagerPin.Trim();
        s.CashierPin = string.IsNullOrWhiteSpace(CashierPin) ? null : CashierPin.Trim();
        s.CallerIdEnabled = CallerIdEnabled;
        s.CallerIdMode = CallerIdMode.Trim();
        s.CallerIdComPort = CallerIdCom.Trim();
        s.QuickNotes = NoteRows.Select(n => new QuickNoteDef { En = n.En.Trim(), Zh = n.Zh.Trim() })
            .Where(n => !string.IsNullOrWhiteSpace(n.En))
            .ToList();
        if (s.QuickNotes.Count == 0)
            s.QuickNotes = QuickKitchenNotes.CreateDefaultList();
        _app.SaveSettings(s);
        _setStatus("Settings saved");
        MenuInfo = $"Items: {_app.Menu.CountItems()} | Last import: {s.LastMenuImportAt ?? "n/a"}";
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private void ApplyBaseUrl()
    {
        var s = _app.GetSettings();
        s.ApplyOnlineBaseUrl(OnlineBaseUrl);
        OrderServerUrl = s.OnlineOrderServerUrl;
        CallbackUrl = s.OnlineCallbackUrl;
        PrintedUrl = s.OnlinePrintedUrl;
        _setStatus("Derived Online URLs from base");
    }

    [RelayCommand]
    private async Task ReimportMenuAsync()
    {
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "Re-import seed menu"))
            return;
        if (!await UiPrompt.ConfirmAsync(
                "Re-import seed menu?",
                "This replaces categories/dishes with the embedded seed. Custom dishes you created may be overwritten or removed. Continue?"))
            return;
        var (cats, items) = _app.MenuSeeder.ImportEmbedded();
        ClearDishEditor();
        Reload();
        _setStatus($"Re-imported menu: {cats} categories, {items} items");
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteEn)) return;
        NoteRows.Add(new QuickNoteEditRow(NewNoteEn.Trim(), NewNoteZh.Trim()));
        NewNoteEn = "";
        NewNoteZh = "";
    }

    [RelayCommand]
    private void RemoveNote(QuickNoteEditRow? row)
    {
        if (row is null) return;
        NoteRows.Remove(row);
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        Save();
        try
        {
            await _app.KitchenPrinter.PrintTestPageAsync();
            _setStatus($"Test print → {KitchenPrinter}");
        }
        catch (Exception ex)
        {
            _setStatus($"Test print failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "Open drawer"))
            return;
        Save();
        try
        {
            await _app.CashDrawer.OpenAsync();
            _setStatus("Drawer pulse sent");
        }
        catch (Exception ex)
        {
            _setStatus($"Drawer failed: {ex.Message}");
        }
    }
}
