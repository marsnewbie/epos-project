using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicWok.Epos.Domain;
using MagicWok.Epos.Services;

namespace MagicWok.Epos.ViewModels;

public partial class SellViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private PosOrder _ticket = new();
    private Dictionary<string, IReadOnlyList<string>> _pendingSelections = new();

    public SellViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
        foreach (var (en, zh) in QuickKitchenNotes.Defaults)
            QuickNotes.Add(new QuickNoteItem(en, zh));
        RefreshMenu();
        NewTicket();
    }

    public ObservableCollection<CategoryTile> CategoryTiles { get; } = [];
    public ObservableCollection<MenuItem> Items { get; } = [];
    public ObservableCollection<CartLine> Lines { get; } = [];
    public ObservableCollection<QuickNoteItem> QuickNotes { get; } = [];
    public ObservableCollection<OptionChoiceChip> OptionChips { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private MenuItem? _selectedItem;
    [ObservableProperty] private CartLine? _selectedLine;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _orderType = "Collection";
    [ObservableProperty] private string _customerName = "";
    [ObservableProperty] private string _customerPhone = "";
    [ObservableProperty] private string _deliveryAddress = "";
    [ObservableProperty] private string _deliveryPostcode = "";
    [ObservableProperty] private string _orderNotes = "";
    [ObservableProperty] private string _lineNotesDraft = "";
    [ObservableProperty] private string _panelStatus = "";
    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _deliveryFee;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private string _cashTenderedText = "";
    [ObservableProperty] private string _changeText = "";
    [ObservableProperty] private string _adHocName = "";
    [ObservableProperty] private string _adHocPrice = "";
    [ObservableProperty] private string _adHocZh = "";
    [ObservableProperty] private string _modifierSummary = "";
    [ObservableProperty] private bool _showModifierPanel;
    [ObservableProperty] private string _modifierTitle = "";
    [ObservableProperty] private bool _isDelivery;
    [ObservableProperty] private bool _isCollection = true;
    [ObservableProperty] private bool _isWalkIn;
    [ObservableProperty] private bool _isEatIn;
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private bool _showAdHoc;
    [ObservableProperty] private string _categoryHeading = "Menu";

    public void RefreshMenu()
    {
        RebuildCategoryTiles();
        if (CategoryTiles.Count > 0)
        {
            if (SelectedCategory is null || CategoryTiles.All(c => c.Category.Id != SelectedCategory.Id))
                SelectCategory(CategoryTiles[0].Category);
            else
                LoadItems();
        }
        else LoadItems();
    }

    private void RebuildCategoryTiles()
    {
        var selectedId = SelectedCategory?.Id;
        CategoryTiles.Clear();
        foreach (var c in _app.Menu.GetCategories())
            CategoryTiles.Add(new CategoryTile(c, c.Id == selectedId));
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        CategoryHeading = value?.Name ?? "Menu";
        RebuildCategoryTiles();
        LoadItems();
    }

    partial void OnSearchTextChanged(string value) => LoadItems();

    private void LoadItems()
    {
        Items.Clear();
        IEnumerable<MenuItem> list = string.IsNullOrWhiteSpace(SearchText)
            ? _app.Menu.GetItems(SelectedCategory?.Id)
            : _app.Menu.Search(SearchText);
        foreach (var i in list) Items.Add(i);
        if (!string.IsNullOrWhiteSpace(SearchText))
            CategoryHeading = $"Search: {SearchText}";
        else if (SelectedCategory is not null)
            CategoryHeading = SelectedCategory.Name;
    }

    [RelayCommand]
    private void SelectCategory(Category? category)
    {
        if (category is null) return;
        SelectedCategory = category;
        SearchText = "";
    }

    [RelayCommand]
    private void SelectItem(MenuItem? item)
    {
        if (item is null) return;
        SelectedItem = item;
        _pendingSelections = LinePricing.DefaultSelections(item);
        RebuildOptionChips();
        ModifierSummary = BuildModifierSummary(item, _pendingSelections);
        if (item.OptionGroups.Count == 0)
        {
            AddSelectedItem();
            return;
        }

        ModifierTitle = string.IsNullOrWhiteSpace(item.MenuNumber)
            ? item.Name
            : $"{item.MenuNumber}  {item.Name}";
        ShowModifierPanel = true;
    }

    [RelayCommand]
    private void CancelModifier()
    {
        ShowModifierPanel = false;
        SelectedItem = null;
        OptionChips.Clear();
        ModifierSummary = "";
    }

    [RelayCommand]
    private void SetOrderType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        OrderType = type;
    }

    [RelayCommand]
    private void ToggleAdHoc() => ShowAdHoc = !ShowAdHoc;

    [RelayCommand]
    private void CashDigit(string? digit)
    {
        if (string.IsNullOrEmpty(digit)) return;
        if (digit == "." && CashTenderedText.Contains('.')) return;
        CashTenderedText += digit;
        UpdateChangePreview();
    }

    [RelayCommand]
    private void CashClear()
    {
        CashTenderedText = "";
        ChangeText = "";
    }

    [RelayCommand]
    private void CashExact()
    {
        CashTenderedText = Total.ToString("0.00");
        ChangeText = "Exact";
    }

    [RelayCommand]
    private void CashQuick(string? amount)
    {
        if (amount is null) return;
        CashTenderedText = amount;
        UpdateChangePreview();
    }

    private void UpdateChangePreview()
    {
        if (decimal.TryParse(CashTenderedText, out var tendered) && tendered >= Total && Total > 0)
            ChangeText = $"Change £{tendered - Total:0.00}";
        else
            ChangeText = "";
    }

    [RelayCommand]
    private void ToggleOption(OptionChoiceChip? chip)
    {
        if (SelectedItem is null || chip is null) return;
        var group = SelectedItem.OptionGroups.FirstOrDefault(g => g.Id == chip.GroupId);
        if (group is null) return;

        var current = _pendingSelections.TryGetValue(group.Id, out var ids) ? ids.ToList() : [];
        if (group.Type is OptionGroupType.Radio or OptionGroupType.Select)
            _pendingSelections[group.Id] = new[] { chip.ChoiceId };
        else
        {
            if (current.Contains(chip.ChoiceId)) current.Remove(chip.ChoiceId);
            else current.Add(chip.ChoiceId);
            _pendingSelections[group.Id] = current;
        }

        RebuildOptionChips();
        ModifierSummary = BuildModifierSummary(SelectedItem, _pendingSelections);
    }

    [RelayCommand]
    private void AddSelectedItem()
    {
        if (SelectedItem is null) return;
        var err = LinePricing.ValidateSelections(SelectedItem, _pendingSelections);
        if (err is not null)
        {
            PanelStatus = err;
            return;
        }

        var line = LinePricing.BuildMenuLine(SelectedItem, 1, _pendingSelections, LineNotesDraft);
        Lines.Add(line);
        LineNotesDraft = "";
        PanelStatus = $"Added {line.Name} £{line.LineTotal:0.00}";
        ShowModifierPanel = false;
        SyncTicketTotals();
    }

    [RelayCommand]
    private void AddAdHoc()
    {
        if (!decimal.TryParse(AdHocPrice, out var price))
        {
            PanelStatus = "Ad-hoc price invalid";
            return;
        }

        var line = LinePricing.BuildAdHocLine(AdHocName, price, 1, AdHocZh, LineNotesDraft);
        Lines.Add(line);
        AdHocName = "";
        AdHocPrice = "";
        AdHocZh = "";
        LineNotesDraft = "";
        SyncTicketTotals();
    }

    [RelayCommand]
    private void ApplyQuickNote(QuickNoteItem? note)
    {
        if (note is null) return;
        var tag = $"{note.En}/{note.Zh}";
        if (SelectedLine is not null)
        {
            SelectedLine.Notes = string.IsNullOrWhiteSpace(SelectedLine.Notes) ? tag : $"{SelectedLine.Notes}; {tag}";
            RefreshLines();
            PanelStatus = $"Note: {tag}";
            return;
        }

        LineNotesDraft = string.IsNullOrWhiteSpace(LineNotesDraft) ? tag : $"{LineNotesDraft}; {tag}";
    }

    [RelayCommand]
    private void QtyPlus()
    {
        if (SelectedLine is null) return;
        SelectedLine.Quantity++;
        LinePricing.RecalculateLine(SelectedLine);
        SyncTicketTotals();
    }

    [RelayCommand]
    private void QtyMinus()
    {
        if (SelectedLine is null) return;
        if (SelectedLine.Quantity <= 1)
        {
            Lines.Remove(SelectedLine);
            SelectedLine = null;
        }
        else
        {
            SelectedLine.Quantity--;
            LinePricing.RecalculateLine(SelectedLine);
        }
        SyncTicketTotals();
    }

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is null) return;
        Lines.Remove(SelectedLine);
        SelectedLine = null;
        SyncTicketTotals();
    }

    [RelayCommand]
    private void ClearTicket() => NewTicket();

    [RelayCommand]
    private async Task SendKitchenAsync()
    {
        try
        {
            var order = BuildOrderFromTicket(PosOrderStatus.Sent);
            _app.Orders.Upsert(order);
            await _app.Print.PrintKitchenAsync(order);
            PanelStatus = $"Kitchen printed {order.OrderNumber}";
            _setStatus(PanelStatus);
            NewTicket();
        }
        catch (Exception ex)
        {
            PanelStatus = $"Send kitchen failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private async Task PayCashAsync()
    {
        try
        {
            var order = BuildOrderFromTicket(PosOrderStatus.Paid);
            order.Tenders.Add(new OrderTender { Type = TenderType.Cash, Amount = order.Total });
            _app.Orders.Upsert(order);

            var settings = _app.GetSettings();
            if (settings.SendKitchenOnSend && !order.KitchenPrinted)
                await _app.Print.PrintKitchenAsync(order);
            if (settings.PrintFrontOnPay)
                await _app.Print.PrintFrontAsync(order);
            if (settings.OpenDrawerOnCash)
                await _app.CashDrawer.OpenAsync();

            ChangeText = decimal.TryParse(CashTenderedText, out var tendered) && tendered >= order.Total
                ? $"Change £{tendered - order.Total:0.00}"
                : "";

            PanelStatus = $"Cash paid {order.OrderNumber} £{order.Total:0.00}";
            _setStatus(PanelStatus);
            NewTicket();
        }
        catch (Exception ex)
        {
            PanelStatus = $"Cash pay failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private async Task PayCardAsync()
    {
        try
        {
            var order = BuildOrderFromTicket(PosOrderStatus.Paid);
            var result = await _app.CardTerminal.StartSaleAsync(order.Total);
            order.Tenders.Add(new OrderTender
            {
                Type = TenderType.CardManual,
                Amount = order.Total,
                Reference = result.Message,
            });
            _app.Orders.Upsert(order);

            var settings = _app.GetSettings();
            if (settings.SendKitchenOnSend && !order.KitchenPrinted)
                await _app.Print.PrintKitchenAsync(order);
            if (settings.PrintFrontOnPay)
                await _app.Print.PrintFrontAsync(order);

            PanelStatus = $"Card (manual) {order.OrderNumber} £{order.Total:0.00}";
            _setStatus(PanelStatus);
            NewTicket();
        }
        catch (Exception ex)
        {
            PanelStatus = $"Card pay failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    public void ApplyCallerId(string phone)
    {
        CustomerPhone = phone;
        var customer = _app.Customers.FindByPhone(phone);
        if (customer is not null)
        {
            CustomerName = customer.Name;
            var addr = customer.Addresses.FirstOrDefault(a => a.IsDefault) ?? customer.Addresses.FirstOrDefault();
            if (addr is not null)
            {
                DeliveryAddress = string.Join(", ", new[] { addr.Line1, addr.Line2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
                DeliveryPostcode = addr.Postcode;
                OrderType = "Delivery";
            }
            PanelStatus = $"Matched {customer.Name}";
        }
        else PanelStatus = $"New caller {phone}";
    }

    private void NewTicket()
    {
        _ticket = new PosOrder();
        Lines.Clear();
        CustomerName = "";
        CustomerPhone = "";
        DeliveryAddress = "";
        DeliveryPostcode = "";
        OrderNotes = "";
        CashTenderedText = "";
        ChangeText = "";
        SyncTicketTotals();
    }

    private PosOrder BuildOrderFromTicket(PosOrderStatus status)
    {
        if (Lines.Count == 0) throw new InvalidOperationException("Ticket is empty.");
        if (OrderType == "Delivery" &&
            string.IsNullOrWhiteSpace(DeliveryAddress) &&
            string.IsNullOrWhiteSpace(DeliveryPostcode))
            throw new InvalidOperationException("Delivery needs address or postcode.");

        _ticket.OrderNumber = string.IsNullOrWhiteSpace(_ticket.OrderNumber)
            ? _app.Settings.AllocateOrderNumber()
            : _ticket.OrderNumber;
        _ticket.OrderType = Enum.Parse<PosOrderType>(OrderType);
        _ticket.Source = PosOrderSource.Pos;
        _ticket.Status = status;
        _ticket.CustomerName = NullIfEmpty(CustomerName);
        _ticket.CustomerPhone = NullIfEmpty(CustomerPhone);
        _ticket.DeliveryAddress = NullIfEmpty(DeliveryAddress);
        _ticket.DeliveryPostcode = NullIfEmpty(DeliveryPostcode);
        _ticket.Notes = NullIfEmpty(OrderNotes);
        _ticket.Lines = Lines.ToList();
        _ticket.DeliveryFee = _ticket.OrderType == PosOrderType.Delivery ? _app.GetSettings().DefaultDeliveryFee : 0;
        LinePricing.RecalculateOrder(_ticket);

        if (!string.IsNullOrWhiteSpace(_ticket.CustomerPhone))
        {
            var existing = _app.Customers.FindByPhone(_ticket.CustomerPhone!);
            var c = existing ?? new Customer { Phone = _ticket.CustomerPhone! };
            if (!string.IsNullOrWhiteSpace(_ticket.CustomerName)) c.Name = _ticket.CustomerName!;
            if (!string.IsNullOrWhiteSpace(_ticket.DeliveryAddress) || !string.IsNullOrWhiteSpace(_ticket.DeliveryPostcode))
            {
                c.Addresses.Add(new CustomerAddress
                {
                    Line1 = _ticket.DeliveryAddress ?? "",
                    Postcode = _ticket.DeliveryPostcode ?? "",
                    IsDefault = c.Addresses.Count == 0,
                });
            }
            _app.Customers.Upsert(c);
            _ticket.CustomerId = c.Id;
        }

        return _ticket;
    }

    private void SyncTicketTotals()
    {
        var tmp = new PosOrder
        {
            Lines = Lines.ToList(),
            DeliveryFee = OrderType == "Delivery" ? _app.GetSettings().DefaultDeliveryFee : 0,
        };
        LinePricing.RecalculateOrder(tmp);
        Subtotal = tmp.Subtotal;
        DeliveryFee = tmp.DeliveryFee;
        Total = tmp.Total;
        LineCount = Lines.Sum(l => l.Quantity);
        RefreshLines();
        UpdateChangePreview();
    }

    private void RefreshLines()
    {
        var snap = Lines.ToList();
        Lines.Clear();
        foreach (var l in snap) Lines.Add(l);
    }

    partial void OnOrderTypeChanged(string value)
    {
        IsCollection = value == "Collection";
        IsDelivery = value == "Delivery";
        IsWalkIn = value == "WalkIn";
        IsEatIn = value == "EatIn";
        SyncTicketTotals();
    }

    private void RebuildOptionChips()
    {
        OptionChips.Clear();
        if (SelectedItem is null) return;
        foreach (var g in LinePricing.GetVisibleOptionGroups(SelectedItem, _pendingSelections))
        {
            foreach (var c in g.Choices.Where(x => x.IsAvailable))
            {
                var selected = _pendingSelections.TryGetValue(g.Id, out var ids) && ids.Contains(c.Id);
                var delta = c.PriceDelta == 0 ? "" : (c.PriceDelta > 0 ? $" +£{c.PriceDelta:0.00}" : $" £{c.PriceDelta:0.00}");
                OptionChips.Add(new OptionChoiceChip(
                    g.Id,
                    c.Id,
                    $"{g.Name}: {c.Label}{delta}",
                    selected));
            }
        }
    }

    private static string BuildModifierSummary(MenuItem item, Dictionary<string, IReadOnlyList<string>> map)
    {
        var parts = new List<string>();
        foreach (var g in LinePricing.GetVisibleOptionGroups(item, map))
        {
            if (!map.TryGetValue(g.Id, out var ids) || ids.Count == 0) continue;
            var labels = g.Choices.Where(c => ids.Contains(c.Id)).Select(c => c.Label);
            parts.Add($"{g.Name}: {string.Join(", ", labels)}");
        }
        return string.Join(" | ", parts);
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record QuickNoteItem(string En, string Zh)
{
    public string Display => $"{En} / {Zh}";
}

public sealed record OptionChoiceChip(string GroupId, string ChoiceId, string Label, bool IsSelected);

public sealed record CategoryTile(Category Category, bool IsSelected)
{
    public string Name => Category.Name;
}
