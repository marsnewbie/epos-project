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
        Reload();
    }

    public ObservableCollection<MenuEditRow> MenuRows { get; } = [];
    public ObservableCollection<QuickNoteEditRow> NoteRows { get; } = [];
    public ObservableCollection<CategoryAdminRow> MenuCategories { get; } = [];
    public ObservableCollection<OptionGroupAdminRow> SelectedItemOptionGroups { get; } = [];

    [ObservableProperty] private CategoryAdminRow? _selectedMenuCategory;
    [ObservableProperty] private MenuEditRow? _selectedMenuItem;
    [ObservableProperty] private string _itemDetailText = "";
    [ObservableProperty] private string _itemOptionsSummary = "";

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
        MenuInfo = $"Items: {_app.Menu.CountItems()} | Last import: {s.LastMenuImportAt ?? "n/a"}";
        ReloadMenuBrowser();
        ReloadNoteRows();
        ReloadShift();
        GoSection(Section);
    }

    private void ReloadMenuBrowser()
    {
        var prevCat = SelectedMenuCategory?.Id;
        var prevItem = SelectedMenuItem?.Item.Id;
        MenuCategories.Clear();
        foreach (var c in _app.Menu.GetCategories(visibleOnly: false))
            MenuCategories.Add(new CategoryAdminRow(c));

        SelectedMenuCategory = MenuCategories.FirstOrDefault(c => c.Id == prevCat)
                               ?? MenuCategories.FirstOrDefault();
        ReloadMenuRows(preferItemId: prevItem);
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

        foreach (var i in items.Take(300))
            MenuRows.Add(new MenuEditRow(i));

        SelectedMenuItem = preferItemId is not null
            ? MenuRows.FirstOrDefault(r => r.Item.Id == preferItemId) ?? MenuRows.FirstOrDefault()
            : MenuRows.FirstOrDefault();
        RefreshItemDetail();
    }

    partial void OnSelectedMenuCategoryChanged(CategoryAdminRow? value)
    {
        if (!string.IsNullOrWhiteSpace(MenuSearch)) return;
        ReloadMenuRows();
    }

    partial void OnSelectedMenuItemChanged(MenuEditRow? value) => RefreshItemDetail();

    private void RefreshItemDetail()
    {
        SelectedItemOptionGroups.Clear();
        if (SelectedMenuItem is null)
        {
            ItemDetailText = "";
            ItemOptionsSummary = "Select a dish";
            return;
        }

        var item = SelectedMenuItem.Item;
        var cat = MenuCategories.FirstOrDefault(c => c.Id == item.CategoryId)?.Name ?? item.CategoryId;
        ItemDetailText =
            $"{item.MenuNumber}  {item.Name}\n" +
            (string.IsNullOrWhiteSpace(item.ItemTranslation) ? "" : $"{item.ItemTranslation}\n") +
            $"Category: {cat}\n" +
            $"Base £{item.BasePrice:0.00} · {(item.IsAvailable ? "On sale" : "86 SOLD OUT")}\n" +
            (string.IsNullOrWhiteSpace(item.Description) ? "" : $"{item.Description}\n");

        if (item.OptionGroups.Count == 0)
        {
            ItemOptionsSummary = "No option groups — simple dish.";
            return;
        }

        ItemOptionsSummary = $"{item.OptionGroups.Count} option group(s)";
        foreach (var g in item.OptionGroups.OrderBy(x => x.SortOrder))
        {
            var type = g.Type switch
            {
                OptionGroupType.Checkbox => "Multi / checkbox",
                OptionGroupType.Select => "Select (single)",
                _ => "Single / radio",
            };
            var rules = g.Type == OptionGroupType.Checkbox
                ? $"min {g.MinSelections ?? 0} · max {g.MaxSelections ?? g.Choices.Count}"
                : (g.Required ? "required" : "optional");
            var when = g.ShowWhen is null
                ? ""
                : $" · shows when parent choice selected";
            var row = new OptionGroupAdminRow
            {
                Title = g.Name,
                Meta = $"{type} · {rules}{(g.Required ? " · REQ" : "")}{when}",
                ChoicesText = string.Join("\n", g.Choices.Select(c =>
                {
                    var p = c.PriceDelta == 0 ? "" : (c.PriceDelta > 0 ? $" +£{c.PriceDelta:0.00}" : $" £{c.PriceDelta:0.00}");
                    var zh = string.IsNullOrWhiteSpace(c.OptionTranslation) ? "" : $" / {c.OptionTranslation}";
                    return $"  • {c.Label}{zh}{p}";
                })),
            };
            SelectedItemOptionGroups.Add(row);
        }
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
        var paid = all.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList();
        var cash = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.Cash).Sum(t => t.Amount);
        var card = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.CardManual).Sum(t => t.Amount);
        var online = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.OnlinePaid).Sum(t => t.Amount);
        var voided = all.Count(o => o.Status == PosOrderStatus.Voided);
        ShiftSummary =
            $"Paid orders: {paid.Count}\n" +
            $"Cash tendered: £{cash:0.00}\n" +
            $"Card (manual): £{card:0.00}\n" +
            $"Online paid: £{online:0.00}\n" +
            $"Gross (order totals): £{paid.Sum(o => o.Total):0.00}\n" +
            $"Voided: {voided}\n" +
            $"Unpaid open: {all.Count(o => o.IsUnpaid)}";
    }

    partial void OnMenuSearchChanged(string value) => ReloadMenuRows();

    [RelayCommand]
    private void ToggleCategoryVisible(CategoryAdminRow? row)
    {
        if (row is null) return;
        row.IsVisible = !row.IsVisible;
        row.Category.IsVisible = row.IsVisible;
        _app.Menu.SetCategoryVisible(row.Id, row.IsVisible);
        _setStatus(row.IsVisible ? $"Category visible: {row.Name}" : $"Category hidden: {row.Name}");
        ReloadMenuBrowser();
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
    private void ReimportMenu()
    {
        var (cats, items) = _app.MenuSeeder.ImportEmbedded();
        Reload();
        _setStatus($"Re-imported menu: {cats} categories, {items} items");
    }

    [RelayCommand]
    private void SaveMenuRow(MenuEditRow? row)
    {
        if (row is null) return;
        if (!decimal.TryParse(row.PriceText, out var price))
        {
            _setStatus("Invalid price");
            return;
        }
        row.Item.BasePrice = price;
        row.Item.IsAvailable = row.IsAvailable;
        _app.Menu.UpsertItem(row.Item);
        _setStatus($"Saved {row.Item.MenuNumber} {row.Item.Name}");
        ReloadMenuRows(preferItemId: row.Item.Id);
    }

    [RelayCommand]
    private void Toggle86(MenuEditRow? row)
    {
        if (row is null) return;
        row.IsAvailable = !row.IsAvailable;
        row.Item.IsAvailable = row.IsAvailable;
        _app.Menu.SetItemAvailable(row.Item.Id, row.IsAvailable);
        _setStatus(row.IsAvailable ? $"Available: {row.Item.Name}" : $"86: {row.Item.Name}");
        ReloadMenuRows(preferItemId: row.Item.Id);
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

public partial class MenuEditRow : ObservableObject
{
    public MenuEditRow(MenuItem item)
    {
        Item = item;
        PriceText = item.BasePrice.ToString("0.00");
        IsAvailable = item.IsAvailable;
    }

    public MenuItem Item { get; }
    public string Label => string.IsNullOrWhiteSpace(Item.MenuNumber)
        ? Item.Name
        : $"{Item.MenuNumber}  {Item.Name}";
    public string OptionsBadge => Item.OptionGroups.Count == 0
        ? ""
        : $"{Item.OptionGroups.Count} opt";
    public string StatusText => IsAvailable ? "On" : "86";

    [ObservableProperty] private string _priceText;
    [ObservableProperty] private bool _isAvailable;
}

public partial class CategoryAdminRow : ObservableObject
{
    public CategoryAdminRow(Category category)
    {
        Category = category;
        IsVisible = category.IsVisible;
    }

    public Category Category { get; }
    public string Id => Category.Id;
    public string Name => Category.Name;

    [ObservableProperty] private bool _isVisible;
    public string VisibilityText => IsVisible ? "Shown" : "Hidden";

    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(VisibilityText));
}

public sealed class OptionGroupAdminRow
{
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string ChoicesText { get; set; } = "";
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
