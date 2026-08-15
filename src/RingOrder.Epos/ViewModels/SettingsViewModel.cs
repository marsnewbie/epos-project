using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;
using RingOrder.Epos.Online;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

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
    public ObservableCollection<StaffRow> StaffMembers { get; } = [];
    public ObservableCollection<PrinterRow> Printers { get; } = [];
    public ObservableCollection<RouteRow> Routes { get; } = [];
    public ObservableCollection<PrintJob> FailedJobs { get; } = [];
    public ObservableCollection<TaxClassRow> TaxClasses { get; } = [];
    public PrintTransport[] Transports { get; } = Enum.GetValues<PrintTransport>();

    /// <summary>Stations a category or dish can be sent to.</summary>
    public string[] PrintClasses { get; } = PrintClass.Known.ToArray();

    /// <summary>The same list plus a blank, for a dish that follows its category.</summary>
    public string[] PrintClassesWithInherit { get; } = ["", .. PrintClass.Known];
    public StaffRole[] StaffRoles { get; } = Enum.GetValues<StaffRole>();
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
    [ObservableProperty] private string _categoryEditPrintClass = Domain.PrintClass.Kitchen;

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
    [ObservableProperty] private StaffRow? _selectedStaff;
    [ObservableProperty] private string _newStaffName = "";
    [ObservableProperty] private StaffRole _newStaffRole = StaffRole.Cashier;
    [ObservableProperty] private string _staffHint = "";
    [ObservableProperty] private string _webTestResult = "";
    [ObservableProperty] private string _printerHint = "";
    [ObservableProperty] private bool _isTax;
    [ObservableProperty] private string _vatNumber = "";
    [ObservableProperty] private bool _pricesIncludeTax = true;
    [ObservableProperty] private string _taxHint = "";
    [ObservableProperty] private string _receiptFooter = "";
    [ObservableProperty] private string _supportSummary = "";
    [ObservableProperty] private string _supportResult = "";
    [ObservableProperty] private bool _hasFailedJobs;
    [ObservableProperty] private bool _isSupport;
    [ObservableProperty] private bool _isPrivacy;
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
        IsSupport = Section == "Support";
        IsPrivacy = Section == "Privacy";
        IsTax = Section == "Tax";
        if (IsMenu) ReloadMenuBrowser();
        if (IsNotes) ReloadNoteRows();
        if (IsShift) ReloadShift();
        if (IsSupport) ReloadSupport();
        if (IsTax) ReloadTax();
        if (IsDelivery) ReloadAddressLookup();
        if (IsPrivacy) ReloadRetention();
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
        ReloadStaff();
        ReloadPrinters();
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
            CategoryEditPrintClass = value.PrintClass;
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
        var online = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.PrepaidOnline).Sum(t => t.Amount);
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
        CategoryEditPrintClass = SelectedMenuCategory.PrintClass;
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
        SelectedMenuCategory.PrintClass = CategoryEditPrintClass;
        SelectedMenuCategory.ApplyToDomain();
        _app.Menu.UpsertCategory(SelectedMenuCategory.Category);
        EditingCategory = false;
        ReloadMenuBrowser();

        // Lines carry the station they were added with, so a re-route only
        // affects what is rung from now on — yesterday's ticket still says what
        // it said.
        _app.Session.Record("menu.category", SelectedMenuCategory.Id,
            $"{CategoryEditName} → {CategoryEditPrintClass}");
        _setStatus($"Saved category {CategoryEditName} — new orders print at {CategoryEditPrintClass}");
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
        if (!await UiPrompt.RequireAsync(_app, Permission.EditMenu, UiText.Pick("Delete category", "删除分类")))
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

        // Option groups are shared, so editing one here edits it everywhere it
        // is used. Saying which dishes those are beats a surprise at the counter.
        var alsoAffected = domain.OptionGroups
            .SelectMany(g => _app.Menu.GetItemNamesUsingGroup(g.Id))
            .Where(name => !string.Equals(name, domain.Name, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _app.Menu.UpsertItem(domain);
        DishEditor.ValidationMessage = "";
        var id = domain.Id;
        ReloadMenuBrowser();
        SelectedMenuItem = MenuRows.FirstOrDefault(r => r.Item.Id == id);
        if (SelectedMenuItem is not null)
            OpenDishEditor(SelectedMenuItem.Item);
        var saved = $"Saved dish {domain.MenuNumber} {domain.Name}".Trim();
        _setStatus(alsoAffected.Count == 0
            ? saved
            : $"{saved} — shared option groups also changed for: {string.Join(", ", alsoAffected.Take(6))}"
              + (alsoAffected.Count > 6 ? $" and {alsoAffected.Count - 6} more" : ""));
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
        if (!await UiPrompt.RequireAsync(_app, Permission.EditMenu, UiText.Pick("Delete dish", "删除菜品")))
            return;
        if (!await UiPrompt.ConfirmAsync("Delete dish?", $"Delete “{DishEditor.Name}”? Orders history keeps past lines; the till will no longer offer this dish."))
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
        var bundle = Directory
            .EnumerateFiles(LocalPaths.ProfileDirectory, "*.ringpos.json")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (bundle is null)
        {
            _setStatus($"No shop bundle found in {LocalPaths.ProfileDirectory}");
            return;
        }

        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Re-import shop bundle", "重新导入配置包")))
            return;
        if (!await UiPrompt.ConfirmAsync(
                "Re-import shop bundle?",
                $"This replaces the whole catalogue with {Path.GetFileName(bundle)}. "
                + "Menu changes made here since the last import are lost. Orders and customers are untouched. Continue?"))
            return;

        try
        {
            var report = _app.BundleImporter.ImportFromFile(bundle);
            ClearDishEditor();
            Reload();
            _setStatus(report.Summary);
            _onSaved?.Invoke();
        }
        catch (Exception ex)
        {
            _setStatus($"Import failed: {ex.Message}");
        }
    }

    // ── Web orders ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pull one order now. Used at install time to prove the credentials work
    /// before the shop opens, and during support when a merchant says an order
    /// has not arrived. Turning the feed on and off is a top-bar control, not a
    /// setting — that decision is made during service, not during setup.
    /// </summary>
    [RelayCommand]
    private async Task TestWebOrdersAsync()
    {
        Save();
        var settings = _app.GetSettings();
        if (string.IsNullOrWhiteSpace(settings.OnlineOrderServerUrl) ||
            string.IsNullOrWhiteSpace(settings.OnlineUsername))
        {
            WebTestResult = UiText.Pick(
                "Set the base URL and the credentials from the website's print settings first.",
                "请先填写网址和网站打印设置里的凭据。");
            return;
        }

        WebTestResult = UiText.Pick("Checking…", "检查中…");
        try
        {
            await _app.OnlinePoller.PollOnceAsync();
            WebTestResult = string.IsNullOrWhiteSpace(_app.OnlinePoller.LastStatus)
                ? UiText.Pick("Connected. No order waiting.", "连接正常，暂无待取订单。")
                : _app.OnlinePoller.LastStatus;
        }
        catch (Exception ex)
        {
            WebTestResult = ex.Message;
        }
    }

    // ── Staff ───────────────────────────────────────────────────────────────

    private void ReloadStaff()
    {
        StaffMembers.Clear();
        foreach (var member in _app.Staff.ListAll(activeOnly: false))
            StaffMembers.Add(new StaffRow(member, member.Id == _app.Session.Staff?.Id));

        var needChange = StaffMembers.Count(r => r.Member.MustChangePin && r.Member.IsActive);
        StaffHint = needChange > 0
            ? UiText.Pick(
                $"{needChange} account(s) still use the PIN we set up. Change them before the shop opens.",
                $"{needChange} 个账号仍在用我们设置的初始 PIN，开店前请修改。")
            : UiText.Pick(
                "Everyone signs in with their own PIN, so voids and payments have a name against them.",
                "每人用自己的 PIN 登录，作废和收款才有名字可查。");
    }

    [RelayCommand]
    private async Task AddStaffAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.ManageStaff, UiText.Pick("Add staff", "添加员工")))
            return;

        var name = NewStaffName.Trim();
        if (name.Length == 0)
        {
            _setStatus(UiText.Pick("Enter a name first", "请先输入姓名"));
            return;
        }

        var pin = await UiPrompt.PromptPinAsync(UiText.Pick($"PIN for {name}", $"{name} 的 PIN"));
        if (string.IsNullOrWhiteSpace(pin)) return;
        if (pin.Length < 4)
        {
            _setStatus(UiText.Pick("A PIN needs at least 4 digits", "PIN 至少 4 位"));
            return;
        }

        // Two people with one PIN means neither can be told apart afterwards.
        if (_app.Staff.Authenticate(pin) is { } clash)
        {
            _setStatus(UiText.Pick(
                $"That PIN already belongs to {clash.Name}",
                $"该 PIN 已被 {clash.Name} 使用"));
            return;
        }

        var (hash, salt) = PinHasher.Hash(pin);
        _app.Staff.Upsert(new StaffMember
        {
            Name = name,
            Role = NewStaffRole,
            PinHash = hash,
            PinSalt = salt,
        });

        _app.Session.Record("staff.add", detail: $"{name} ({NewStaffRole})");
        NewStaffName = "";
        ReloadStaff();
        _setStatus(UiText.Pick($"Added {name}", $"已添加 {name}"));
    }

    [RelayCommand]
    private async Task ChangeStaffPinAsync(StaffRow? row)
    {
        if (row is null) return;

        // Anyone may change their own PIN; changing someone else's is a manager job.
        var isSelf = row.Member.Id == _app.Session.Staff?.Id;
        if (!isSelf && !await UiPrompt.RequireAsync(
                _app, Permission.ManageStaff, UiText.Pick("Change another PIN", "修改他人 PIN")))
            return;

        var pin = await UiPrompt.PromptPinAsync(
            UiText.Pick($"New PIN for {row.Member.Name}", $"{row.Member.Name} 的新 PIN"));
        if (string.IsNullOrWhiteSpace(pin)) return;
        if (pin.Length < 4)
        {
            _setStatus(UiText.Pick("A PIN needs at least 4 digits", "PIN 至少 4 位"));
            return;
        }

        if (_app.Staff.Authenticate(pin) is { } clash && clash.Id != row.Member.Id)
        {
            _setStatus(UiText.Pick(
                $"That PIN already belongs to {clash.Name}",
                $"该 PIN 已被 {clash.Name} 使用"));
            return;
        }

        _app.Staff.SetPin(row.Member.Id, pin);
        _app.Session.Record("staff.pin", row.Member.Id, row.Member.Name);
        ReloadStaff();
        _setStatus(UiText.Pick($"PIN changed for {row.Member.Name}", $"已修改 {row.Member.Name} 的 PIN"));
    }

    [RelayCommand]
    private async Task SetStaffRoleAsync(StaffRow? row)
    {
        if (row is null) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.ManageStaff, UiText.Pick("Change role", "修改角色")))
            return;

        row.Member.Role = row.SelectedRole;
        _app.Staff.Upsert(row.Member);
        _app.Session.Record("staff.role", row.Member.Id, $"{row.Member.Name} -> {row.SelectedRole}");
        ReloadStaff();
        _setStatus(UiText.Pick(
            $"{row.Member.Name} is now {row.SelectedRole}",
            $"{row.Member.Name} 现在是 {row.SelectedRole}"));
    }

    /// <summary>
    /// Staff are deactivated, never deleted: their name is attached to every
    /// order they took, and a report that cannot name the person who rang a sale
    /// is not worth printing.
    /// </summary>
    [RelayCommand]
    private async Task ToggleStaffActiveAsync(StaffRow? row)
    {
        if (row is null) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.ManageStaff, UiText.Pick("Change staff access", "修改员工状态")))
            return;

        if (row.Member.Id == _app.Session.Staff?.Id)
        {
            _setStatus(UiText.Pick("You cannot switch yourself off", "不能停用自己"));
            return;
        }

        var stillActive = _app.Staff.ListAll()
            .Count(m => m.Id != row.Member.Id && m.Can(Permission.ManageStaff));
        if (row.Member.IsActive && row.Member.Can(Permission.ManageStaff) && stillActive == 0)
        {
            _setStatus(UiText.Pick(
                "That is the last manager — the till would lock everyone out of Settings",
                "这是最后一个经理，停用后没人能进设置"));
            return;
        }

        row.Member.IsActive = !row.Member.IsActive;
        _app.Staff.Upsert(row.Member);
        _app.Session.Record("staff.active", row.Member.Id, $"{row.Member.Name} active={row.Member.IsActive}");
        ReloadStaff();
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
        var device = Printers.FirstOrDefault(p => p.IsEnabled)?.Device;
        if (device is null)
        {
            _setStatus(UiText.Pick("Add a printer first", "请先添加打印机"));
            return;
        }
        await TestPrinterAsync(Printers.First(p => p.Device.Id == device.Id));
    }

    // ── Tax ─────────────────────────────────────────────────────────────────

    private void ReloadTax()
    {
        var settings = _app.GetSettings();
        VatNumber = settings.VatNumber;
        PricesIncludeTax = settings.PricesIncludeTax;
        ReceiptFooter = string.Join("\n", settings.ReceiptFooterLines);

        TaxClasses.Clear();
        foreach (var taxClass in _app.Menu.GetTaxClasses())
            TaxClasses.Add(new TaxClassRow(taxClass));

        TaxHint = string.IsNullOrWhiteSpace(VatNumber)
            ? UiText.Pick(
                "No VAT number, so receipts show no VAT — which is right for a shop under the registration threshold. Add the number when the shop registers.",
                "未填 VAT 号，小票不显示税额——低于登记门槛的店本就该如此。店铺登记后再填。")
            : UiText.Pick(
                "Receipts show the VAT number and a breakdown by rate. UK takeaway: hot food is standard-rated, some cold food is zero-rated.",
                "小票会显示 VAT 号和分税率明细。英国外卖：热食标准税率，部分冷食零税率。");
    }

    [RelayCommand]
    private async Task SaveTaxAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Change VAT settings", "修改税务设置")))
            return;

        var settings = _app.GetSettings();
        settings.VatNumber = VatNumber.Trim();
        settings.PricesIncludeTax = PricesIncludeTax;
        settings.ReceiptFooterLines = ReceiptFooter
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _app.SaveSettings(settings);

        _app.Menu.ReplaceTaxClasses(TaxClasses.Select(r => r.ToDomain()).ToList());
        _app.Session.Record("settings.tax",
            detail: $"VAT {(settings.VatNumber.Length == 0 ? "not registered" : settings.VatNumber)}");

        ReloadTax();
        _setStatus(UiText.Pick("VAT settings saved", "税务设置已保存"));
    }

    // ── Address lookup ──────────────────────────────────────────────────────

    public ObservableCollection<AddressProviderOption> AddressProviders { get; } = [];

    [ObservableProperty] private AddressProviderOption? _selectedAddressProvider;
    [ObservableProperty] private string _addressApiKey = "";
    [ObservableProperty] private bool _addressCacheEnabled = true;
    [ObservableProperty] private bool _addressNeedsKey;
    [ObservableProperty] private string _addressProviderHint = "";
    [ObservableProperty] private string _addressCacheSummary = "";
    [ObservableProperty] private string _addressTestPostcode = "";
    [ObservableProperty] private string _addressTestResult = "";

    partial void OnSelectedAddressProviderChanged(AddressProviderOption? value)
    {
        AddressNeedsKey = value is not null && AddressProviderNames.NeedsApiKey(value.Key);
        AddressProviderHint = AddressProviderNames.Describe(value?.Key ?? AddressProviderNames.None);
    }

    private void ReloadAddressLookup()
    {
        var settings = _app.GetSettings();

        if (AddressProviders.Count == 0)
            foreach (var key in AddressProviderNames.All)
                AddressProviders.Add(new AddressProviderOption(key, LabelFor(key)));

        SelectedAddressProvider =
            AddressProviders.FirstOrDefault(p => p.Key == settings.AddressLookupProvider)
            ?? AddressProviders[0];

        AddressApiKey = settings.AddressLookupApiKey;
        AddressCacheEnabled = settings.AddressLookupCacheEnabled;

        if (AddressTestPostcode.Length == 0)
            AddressTestPostcode = settings.ShopPostcode;

        RefreshAddressCacheSummary();
    }

    private static string LabelFor(string key) => key switch
    {
        AddressProviderNames.PostcodesIo => "postcodes.io — free, postcode check only",
        AddressProviderNames.GetAddressIo => "getAddress.io — full addresses",
        AddressProviderNames.IdealPostcodes => "Ideal Postcodes — full addresses",
        _ => "Off — type addresses by hand",
    };

    /// <summary>
    /// The cache made visible. A merchant looking at a lookup bill should be able
    /// to see how many calls they did not pay for.
    /// </summary>
    private void RefreshAddressCacheSummary()
    {
        var stats = _app.AddressCache.Stats();
        AddressCacheSummary = stats.Postcodes == 0
            ? UiText.Pick(
                "Nothing saved yet. Each postcode is looked up once and then kept, so a shop stops paying for the streets it already delivers to.",
                "暂无缓存。每个邮编只查一次并永久保存，常送的街道之后不再产生查询费用。")
            : UiText.Pick(
                $"{stats.Postcodes} postcodes saved, reused {stats.Hits} times — lookups that cost nothing.",
                $"已保存 {stats.Postcodes} 个邮编，命中 {stats.Hits} 次——这些查询没有花钱。");
    }

    [RelayCommand]
    private async Task SaveAddressLookupAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings,
                UiText.Pick("Change postcode lookup", "修改邮编查询设置")))
            return;

        var settings = _app.GetSettings();
        settings.AddressLookupProvider = SelectedAddressProvider?.Key ?? AddressProviderNames.None;
        settings.AddressLookupApiKey = AddressApiKey.Trim();
        settings.AddressLookupCacheEnabled = AddressCacheEnabled;
        _app.SaveSettings(settings);

        // The key is billable, so it is never written to the audit trail.
        _app.Session.Record("settings.address-lookup", detail: settings.AddressLookupProvider);

        ReloadAddressLookup();
        _setStatus(UiText.Pick("Postcode lookup saved", "邮编查询设置已保存"));
    }

    /// <summary>
    /// Proves the setup against a real postcode before a shift depends on it.
    /// Deliberately bypasses the cache — the point is to prove the provider
    /// answers, and a cached hit would prove nothing.
    /// </summary>
    [RelayCommand]
    private async Task TestAddressLookupAsync()
    {
        var provider = SelectedAddressProvider?.Key ?? AddressProviderNames.None;
        if (provider == AddressProviderNames.None)
        {
            AddressTestResult = UiText.Pick(
                "Lookup is off — nothing to test.", "查询已关闭，无需测试。");
            return;
        }

        var postcode = UkPostcode.Normalise(AddressTestPostcode);
        if (!postcode.IsValid)
        {
            AddressTestResult = UiText.Pick(
                $"\"{AddressTestPostcode}\" is not a UK postcode.",
                $"“{AddressTestPostcode}”不是有效的英国邮编。");
            return;
        }

        AddressTestResult = UiText.Pick("Testing…", "测试中…");

        var lookup = AddressLookupFactory.Create(provider, AddressApiKey.Trim());
        var result = await lookup.FindAsync(postcode);

        AddressTestResult = result.Status switch
        {
            AddressLookupStatus.Ok when result.HasCandidates =>
                $"✓ {result.Candidates.Count} — {result.Candidates[0].Display}",
            AddressLookupStatus.Ok => $"✓ {result.Message}",
            _ => $"✗ {result.Message}",
        };
    }

    [RelayCommand]
    private async Task ClearAddressCacheAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings,
                UiText.Pick("Clear saved addresses", "清空已保存的地址")))
            return;

        var removed = _app.AddressCache.Clear();
        RefreshAddressCacheSummary();
        _setStatus(UiText.Pick($"Cleared {removed} saved postcodes", $"已清空 {removed} 个邮编缓存"));
    }

    // ── Customer data and retention ─────────────────────────────────────────

    [ObservableProperty] private int _retentionMonths;
    [ObservableProperty] private bool _retentionAutomatic;
    [ObservableProperty] private string _retentionSummary = "";
    [ObservableProperty] private bool _hasDormantCustomers;

    partial void OnRetentionMonthsChanged(int value) => RefreshRetentionSummary();

    private void ReloadRetention()
    {
        var settings = _app.GetSettings();
        RetentionMonths = settings.CustomerRetentionMonths;
        RetentionAutomatic = settings.CustomerRetentionAutomatic;
        RefreshRetentionSummary();
    }

    /// <summary>
    /// States the obligation and the count, and leaves the decision alone. The
    /// shop is the data controller; the till's job is to make the choice
    /// informed, not to make it for them.
    /// </summary>
    private void RefreshRetentionSummary()
    {
        var total = _app.Customers.Count();

        if (RetentionMonths <= 0)
        {
            HasDormantCustomers = false;
            RetentionSummary = UiText.Pick(
                $"{total} customers on file, kept indefinitely. UK GDPR asks that personal data is not held for longer than it is needed — set a period to see how many are past it.",
                $"通讯录中有 {total} 位客户，目前永久保留。英国 GDPR 要求个人数据不得保存超过必要期限——设置一个期限即可查看有多少已超期。");
            return;
        }

        var dormant = _app.Retention.FindDormant(RetentionMonths, DateTimeOffset.Now);
        HasDormantCustomers = dormant.Count > 0;

        RetentionSummary = dormant.Count == 0
            ? UiText.Pick(
                $"{total} customers on file, none inactive for more than {RetentionMonths} months.",
                $"通讯录中有 {total} 位客户，没有超过 {RetentionMonths} 个月未下单的。")
            : UiText.Pick(
                $"{dormant.Count} of {total} customers have not ordered in over {RetentionMonths} months. Erasing them removes names, phone numbers and saved addresses. Orders, takings and VAT are kept — HMRC requires six years.",
                $"{total} 位客户中有 {dormant.Count} 位超过 {RetentionMonths} 个月未下单。清除会删除姓名、电话和已存地址；订单、营业额和税额保留——HMRC 要求保存六年。");
    }

    [RelayCommand]
    private async Task SaveRetentionAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings,
                UiText.Pick("Change customer retention", "修改客户数据保留期")))
            return;

        var settings = _app.GetSettings();
        settings.CustomerRetentionMonths = Math.Clamp(RetentionMonths, 0, 120);
        settings.CustomerRetentionAutomatic = RetentionAutomatic && settings.CustomerRetentionMonths > 0;
        _app.SaveSettings(settings);

        _app.Session.Record("settings.retention",
            detail: settings.CustomerRetentionMonths == 0
                ? "no automatic removal"
                : $"{settings.CustomerRetentionMonths} months, automatic {settings.CustomerRetentionAutomatic}");

        ReloadRetention();
        _setStatus(UiText.Pick("Retention saved", "保留期已保存"));
    }

    /// <summary>
    /// Erases every dormant record now. Deliberately a separate, explicit action
    /// rather than something the Save button does on the way past.
    /// </summary>
    [RelayCommand]
    private async Task EraseDormantAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings,
                UiText.Pick("Erase dormant customers", "清除超期客户数据")))
            return;

        var dormant = _app.Retention.FindDormant(RetentionMonths, DateTimeOffset.Now);
        if (dormant.Count == 0)
        {
            _setStatus(UiText.Pick("Nothing to erase", "没有需要清除的记录"));
            return;
        }

        var outcome = _app.Retention.Erase(dormant.Select(d => d.Id).ToList());

        // Counts only. An audit line that repeated the names would put the data
        // straight back into the record it was just removed from.
        _app.Session.Record("customers.erased.retention", detail: outcome.Summary);
        AppLog.Info("privacy", $"retention sweep: {outcome.Summary}");

        ReloadRetention();
        _setStatus(UiText.Pick($"Erased {outcome.Customers} customers", $"已清除 {outcome.Customers} 位客户"));
    }

    // ── Support ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What someone on a remote session needs, without asking the merchant to
    /// read anything out. Refreshed on entry, because a stale diagnostic is
    /// worse than none.
    /// </summary>
    private void ReloadSupport()
    {
        var devices = _app.PrintDevices.GetDevices();
        var faults = _app.PrintQueue.Faults;
        var abandoned = _app.PrintJobs.GetAbandoned();

        SupportSummary = string.Join("\n",
        [
            $"Version      {AppLog.AppVersion}",
            $"Machine      {Environment.MachineName}",
            $"Data         {LocalPaths.RootDirectory}",
            $"Schema       {SchemaVersionText()}",
            $"Shop         {_app.GetSettings().ShopName} · {_app.Menu.CountItems()} dishes",
            $"Printers     {devices.Count(d => d.IsEnabled)} on, {faults.Count} reporting a fault",
            $"Print queue  {_app.PrintJobs.CountWaiting()} waiting, {abandoned.Count} given up",
            $"Web orders   {(_app.OnlinePoller.IsRunning ? "on" : "off")} · {_app.OnlinePoller.LastStatus}",
            $"Last backup  {(_app.Backups.LastBackupAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "none yet")}",
            $"Logs         {AppLog.Directory}",
        ]);

        FailedJobs.Clear();
        foreach (var job in abandoned)
            FailedJobs.Add(job);
        HasFailedJobs = FailedJobs.Count > 0;
    }

    private string SchemaVersionText()
    {
        try
        {
            using var conn = _app.Db.Open();
            return $"{SchemaMigrations.CurrentVersion(conn)} of {SchemaMigrations.LatestVersion}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var path = AppLog.ExportDiagnostics(_app);
            SupportResult = UiText.Pick($"Written to {path}", $"已写入 {path}");
        }
        catch (Exception ex)
        {
            SupportResult = ex.Message;
        }
    }

    [RelayCommand]
    private void BackupNow()
    {
        var path = _app.Backups.BackupNow();
        SupportResult = path is null
            ? UiText.Pick($"Backup failed: {_app.Backups.LastError}", $"备份失败：{_app.Backups.LastError}")
            : UiText.Pick($"Backed up to {path}", $"已备份到 {path}");
        ReloadSupport();
    }

    /// <summary>
    /// Put a ticket the printer gave up on back in the queue. Deliberate, and
    /// after someone has looked at the printer — an automatic retry that never
    /// stops is how a kitchen ends up with forty copies of one order.
    /// </summary>
    [RelayCommand]
    private void RetryFailedJob(PrintJob? job)
    {
        if (job is null) return;
        _app.PrintJobs.Requeue(job);
        _app.PrintQueue.Wake();
        _app.Session.Record("print.retry", job.OrderId, $"{job.OrderNumber} → {job.DeviceId}");
        SupportResult = UiText.Pick($"Order {job.OrderNumber} queued again", $"订单 {job.OrderNumber} 已重新排队");
        ReloadSupport();
    }

    // ── Printers ────────────────────────────────────────────────────────────

    private void ReloadPrinters()
    {
        Printers.Clear();
        foreach (var device in _app.PrintDevices.GetDevices())
            Printers.Add(new PrinterRow(device));

        var devices = _app.PrintDevices.GetDevices();
        Routes.Clear();
        foreach (var route in _app.PrintDevices.GetRoutes())
            Routes.Add(new RouteRow(route, devices));

        PrinterHint = Printers.Count == 0
            ? UiText.Pick(
                "No printers yet. Add the counter printer first — it is the one the cash drawer plugs into.",
                "还没有打印机。先加前台那台——钱箱是插在它上面的。")
            : UiText.Pick(
                "Kitchen printers are best on the network: no Windows spooler to jam, and the printer can report that it is out of paper.",
                "后厨打印机建议走网口：不经过 Windows 打印后台，而且能报告缺纸。");
    }

    [RelayCommand]
    private async Task AddPrinterAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Add printer", "添加打印机")))
            return;

        var device = new PrintDevice
        {
            Name = UiText.Pick($"Printer {Printers.Count + 1}", $"打印机 {Printers.Count + 1}"),
            HasCashDrawer = Printers.Count == 0,
        };
        _app.PrintDevices.UpsertDevice(device, Printers.Count);

        // A shop with one printer and no rules still has to print, so the first
        // device gets the defaults rather than a working till that prints nothing.
        if (_app.PrintDevices.GetRoutes().Count == 0)
            foreach (var route in PrintRouting.DefaultRoutes(_app.PrintDevices.GetDevices()))
                _app.PrintDevices.UpsertRoute(route);

        ReloadPrinters();
        _setStatus(UiText.Pick("Printer added — set its connection, then test it", "已添加打印机 — 填写连接方式后测试"));
    }

    [RelayCommand]
    private void SavePrinter(PrinterRow? row)
    {
        if (row is null) return;
        _app.PrintDevices.UpsertDevice(row.ToDomain(), Printers.IndexOf(row));
        _app.Session.Record("printer.save", row.Device.Id, $"{row.Device.Name} {row.Device.Transport} {row.Device.Address}");
        ReloadPrinters();
        _setStatus(UiText.Pick($"Saved {row.Device.Name}", $"已保存 {row.Device.Name}"));
    }

    [RelayCommand]
    private async Task DeletePrinterAsync(PrinterRow? row)
    {
        if (row is null) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Remove printer", "删除打印机")))
            return;
        if (!await UiPrompt.ConfirmAsync(
                UiText.Pick("Remove printer?", "删除打印机？"),
                UiText.Pick(
                    $"Remove {row.Device.Name}? Any rule that sends tickets to it is removed too, so check what is left prints.",
                    $"删除 {row.Device.Name}？指向它的出单规则会一并删除，请确认剩下的规则仍能出单。")))
            return;

        _app.PrintDevices.DeleteDevice(row.Device.Id);
        ReloadPrinters();
        _setStatus(UiText.Pick("Printer removed", "打印机已删除"));
    }

    /// <summary>
    /// Reach the printer, then put paper through it. Both matter: a device can
    /// answer on the network and still have an open cover.
    /// </summary>
    [RelayCommand]
    private async Task TestPrinterAsync(PrinterRow? row)
    {
        if (row is null) return;
        var device = row.ToDomain();
        _app.PrintDevices.UpsertDevice(device, Printers.IndexOf(row));

        row.Status = UiText.Pick("Checking…", "检查中…");
        row.StatusIsGood = false;

        var transport = PrintTransports.For(device.Transport);
        if (!await transport.IsReachableAsync(device))
        {
            row.Status = UiText.Pick("Cannot reach it — check the address", "连不上 — 请检查地址");
            return;
        }

        if (transport is TcpPrintTransport tcp && await tcp.QueryStatusAsync(device) is { } status && !status.IsReady)
        {
            row.Status = UiText.Pick($"Reached it, but {status.Describe()}", $"能连上，但{(status.OutOfPaper ? "缺纸" : "开盖")}");
            return;
        }

        try
        {
            await _app.Print.TestDeviceAsync(device);
            row.Status = UiText.Pick("Test page sent — check the paper", "已送出测试页 — 请看纸");
            row.StatusIsGood = true;
        }
        catch (Exception ex)
        {
            row.Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddRouteAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Add print rule", "添加出单规则")))
            return;

        var devices = _app.PrintDevices.GetDevices(enabledOnly: true);
        if (devices.Count == 0)
        {
            _setStatus(UiText.Pick("Add a printer first", "请先添加打印机"));
            return;
        }

        _app.PrintDevices.UpsertRoute(new PrintRoute
        {
            SortOrder = Routes.Count,
            Document = PrintDocument.Kitchen,
            DeviceId = devices[0].Id,
        });
        ReloadPrinters();
    }

    [RelayCommand]
    private void SaveRoute(RouteRow? row)
    {
        if (row is null) return;
        var route = row.ToDomain();
        _app.PrintDevices.UpsertRoute(route);
        _app.Session.Record("print.route", route.Id, route.Describe(_app.PrintDevices.GetDeviceMap()));
        ReloadPrinters();
        _setStatus(UiText.Pick("Print rule saved", "出单规则已保存"));
    }

    [RelayCommand]
    private async Task DeleteRouteAsync(RouteRow? row)
    {
        if (row is null) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings, UiText.Pick("Remove print rule", "删除出单规则")))
            return;

        // The last kitchen rule going is worth a warning: without one, tickets
        // fall back to whichever printer happens to be first.
        var remaining = Routes.Count(r => r.Route.Id != row.Route.Id && r.Document == PrintDocument.Kitchen);
        if (row.Document == PrintDocument.Kitchen && remaining == 0 &&
            !await UiPrompt.ConfirmAsync(
                UiText.Pick("Remove the last kitchen rule?", "删除最后一条厨房规则？"),
                UiText.Pick(
                    "Kitchen tickets will go to whichever printer is first in the list. Continue?",
                    "厨房票将送到列表中的第一台打印机。继续？")))
            return;

        _app.PrintDevices.DeleteRoute(row.Route.Id);
        ReloadPrinters();
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.OpenDrawerWithoutSale, UiText.Pick("Open drawer", "开钱箱")))
            return;
        Save();
        try
        {
            await _app.Print.OpenDrawerAsync();
            _setStatus("Drawer pulse sent");
        }
        catch (Exception ex)
        {
            _setStatus($"Drawer failed: {ex.Message}");
        }
    }
}
