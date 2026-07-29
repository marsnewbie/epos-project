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
    private readonly Action? _goOrders;
    private PosOrder _ticket = new();
    private Dictionary<string, IReadOnlyList<string>> _pendingSelections = new();

    public SellViewModel(AppServices app, Action<string> setStatus, Action? goOrders = null)
    {
        _app = app;
        _setStatus = setStatus;
        _goOrders = goOrders;
        ReloadQuickNotes();
        RefreshMenu();
        NewTicket(force: true);
        RefreshUiLabels();
    }

    public ObservableCollection<CategoryTile> CategoryTiles { get; } = [];
    public ObservableCollection<MenuItem> Items { get; } = [];
    public ObservableCollection<CartLine> Lines { get; } = [];
    public ObservableCollection<QuickNoteItem> QuickNotes { get; } = [];
    public ObservableCollection<OptionChoiceChip> OptionChips { get; } = [];
    public ObservableCollection<PosOrder> HeldOrders { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private MenuItem? _selectedItem;
    [ObservableProperty] private CartLine? _selectedLine;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _dishNumberText = "";
    [ObservableProperty] private string _orderType = "Collection";
    [ObservableProperty] private string _customerName = "";
    [ObservableProperty] private string _customerPhone = "";
    [ObservableProperty] private string _deliveryAddress = "";
    [ObservableProperty] private string _deliveryPostcode = "";
    [ObservableProperty] private string _tableNumber = "";
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
    [ObservableProperty] private string _ticketHeader = "NEW TICKET";
    [ObservableProperty] private string _ticketStatusBadge = "DRAFT";
    [ObservableProperty] private bool _hasUnsentLines;
    [ObservableProperty] private bool _ticketIsSent;
    [ObservableProperty] private bool _canEditSelectedLine = true;
    [ObservableProperty] private string _sendButtonText = "SEND KITCHEN";
    [ObservableProperty] private string _lblPhoneOrder = "Phone order";
    [ObservableProperty] private string _lblHeld = "Held";
    [ObservableProperty] private string _lblNew = "New";
    [ObservableProperty] private string _lblClear = "Clear";
    [ObservableProperty] private string _lblHold = "HOLD";
    [ObservableProperty] private string _lblPayCash = "CASH";
    [ObservableProperty] private string _lblPayCard = "CARD";
    [ObservableProperty] private bool _showCashPanel;
    [ObservableProperty] private bool _showHeldPanel;
    [ObservableProperty] private bool _showCustomerMore;
    [ObservableProperty] private bool _isTicketEmpty = true;
    [ObservableProperty] private int _heldCount;
    [ObservableProperty] private string _ticketNumberText = "NEW";
    [ObservableProperty] private string _customerSummary = "";
    [ObservableProperty] private string _lblConfirmCash = "CONFIRM CASH";
    [ObservableProperty] private string _lblBack = "Back";
    [ObservableProperty] private string _emptyTicketHint = "Tap dishes to build ticket";
    [ObservableProperty] private string _heldButtonText = "Held";

    public void RefreshUiLabels()
    {
        var zh = _app.GetSettings().UiLanguage == "zh";
        LblPhoneOrder = zh ? "电话单" : "Phone order";
        LblHeld = zh ? "挂单" : "Held";
        LblNew = zh ? "新单" : "New";
        LblClear = zh ? "清空" : "Clear";
        LblHold = zh ? "挂起" : "HOLD";
        LblPayCash = zh ? "现金" : "CASH";
        LblPayCard = zh ? "刷卡" : "CARD";
        LblConfirmCash = zh ? "确认收现" : "CONFIRM CASH";
        LblBack = zh ? "返回" : "Back";
        EmptyTicketHint = zh ? "点菜开始建单" : "Tap dishes to build ticket";
        UpdateTicketChrome();
    }

    public void ReloadQuickNotes()
    {
        QuickNotes.Clear();
        var notes = _app.GetSettings().QuickNotes;
        if (notes.Count == 0) notes = QuickKitchenNotes.CreateDefaultList();
        foreach (var n in notes)
            QuickNotes.Add(new QuickNoteItem(n.En, n.Zh));
    }

    public void RefreshHeldList()
    {
        HeldOrders.Clear();
        foreach (var o in _app.Orders.GetTodayFiltered("held"))
            HeldOrders.Add(o);
        HeldCount = HeldOrders.Count;
        var zh = _app.GetSettings().UiLanguage == "zh";
        HeldButtonText = HeldCount > 0
            ? (zh ? $"挂单 ({HeldCount})" : $"Held ({HeldCount})")
            : (zh ? "挂单" : "Held");
    }

    [RelayCommand]
    private void ToggleHeldPanel()
    {
        ShowHeldPanel = !ShowHeldPanel;
        if (ShowHeldPanel) RefreshHeldList();
    }

    [RelayCommand]
    private void ToggleCustomerMore() => ShowCustomerMore = !ShowCustomerMore;

    [RelayCommand]
    private void BeginCashPay()
    {
        if (Lines.Count == 0)
        {
            PanelStatus = "Ticket is empty";
            return;
        }
        ShowCashPanel = true;
        if (string.IsNullOrWhiteSpace(CashTenderedText))
            CashExact();
        else
            UpdateChangePreview();
    }

    [RelayCommand]
    private void CancelCashPay()
    {
        ShowCashPanel = false;
        CashTenderedText = "";
        ChangeText = "";
    }

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

    partial void OnSelectedLineChanged(CartLine? value)
    {
        CanEditSelectedLine = value is null || !value.KitchenSent;
    }

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
        if (!item.IsAvailable)
        {
            PanelStatus = $"86 — {item.Name} sold out";
            return;
        }
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
    private async Task EightySixItemAsync(MenuItem? item)
    {
        if (item is null) return;
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "86 / sold out"))
            return;
        item.IsAvailable = false;
        _app.Menu.SetItemAvailable(item.Id, false);
        LoadItems();
        PanelStatus = $"86 {item.MenuNumber} {item.Name}";
        _setStatus(PanelStatus);
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
    private void StartPhoneOrder()
    {
        OrderType = "Collection";
        CustomerName = "";
        CustomerPhone = "";
        DeliveryAddress = "";
        DeliveryPostcode = "";
        PanelStatus = "Phone order — enter name/phone or pick from Customers";
        _setStatus(PanelStatus);
    }

    [RelayCommand]
    private void AddByDishNumber()
    {
        var num = DishNumberText.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(num))
        {
            PanelStatus = "Enter dish # then Add";
            return;
        }
        var item = _app.Menu.FindByMenuNumber(num);
        if (item is null)
        {
            PanelStatus = $"No dish #{num}";
            return;
        }
        DishNumberText = "";
        SelectItem(item);
    }

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
        SelectedLine = line;
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
        SelectedLine = line;
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
        var target = SelectedLine ?? Lines.LastOrDefault();
        if (target is null)
        {
            PanelStatus = "Add a dish first — note binds to last line";
            return;
        }
        if (target.KitchenSent)
        {
            PanelStatus = "Line already sent — cannot edit notes";
            return;
        }

        target.Notes = string.IsNullOrWhiteSpace(target.Notes) ? tag : $"{target.Notes}; {tag}";
        SelectedLine = target;
        RefreshLines();
        PanelStatus = $"Note → {target.Name}: {tag}";
    }

    [RelayCommand]
    private void QtyPlus()
    {
        if (SelectedLine is null) return;
        if (SelectedLine.KitchenSent)
        {
            PanelStatus = "Sent line locked — void or add new line";
            return;
        }
        SelectedLine.Quantity++;
        LinePricing.RecalculateLine(SelectedLine);
        SyncTicketTotals();
    }

    [RelayCommand]
    private void QtyMinus()
    {
        if (SelectedLine is null) return;
        if (SelectedLine.KitchenSent)
        {
            PanelStatus = "Sent line locked — use Void for corrections";
            return;
        }
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
        if (SelectedLine.KitchenSent)
        {
            PanelStatus = "Sent line locked — Void order (PIN) to cancel";
            return;
        }
        Lines.Remove(SelectedLine);
        SelectedLine = null;
        SyncTicketTotals();
    }

    [RelayCommand]
    private async Task ClearTicketAsync()
    {
        if (Lines.Count > 0 || !string.IsNullOrWhiteSpace(_ticket.OrderNumber))
        {
            if (!await UiPrompt.ConfirmAsync("Clear ticket?", "Clear current ticket? Unsaved unpaid lines will be lost."))
                return;
        }
        NewTicket(force: true);
        PanelStatus = "Ticket cleared";
    }

    [RelayCommand]
    private async Task NewTicketAsync()
    {
        if (Lines.Count > 0 && _ticket.Status is PosOrderStatus.Draft or PosOrderStatus.Sent)
        {
            if (!await UiPrompt.ConfirmAsync("New ticket?", "Current ticket stays in Orders if already sent. Start a blank ticket?"))
                return;
            if (_ticket.Status == PosOrderStatus.Draft && Lines.Count > 0)
            {
                // leave draft unsaved unless already persisted
            }
        }
        NewTicket(force: true);
        PanelStatus = "New ticket";
    }

    [RelayCommand]
    private async Task SendKitchenAsync()
    {
        try
        {
            ValidateForSendOrPay(requireAddress: true);
            var unsentOnly = Lines.Any(l => l.KitchenSent) && Lines.Any(l => !l.KitchenSent);
            if (!Lines.Any(l => !l.KitchenSent) && Lines.Any(l => l.KitchenSent))
            {
                PanelStatus = "Nothing new to send — add dishes first";
                return;
            }

            var order = PersistTicket(PosOrderStatus.Sent);
            await _app.Print.PrintKitchenAsync(order, unsentOnly: unsentOnly);
            LoadTicket(order);
            PanelStatus = unsentOnly
                ? $"Sent NEW items · {order.OrderNumber}"
                : $"Kitchen SENT · {order.OrderNumber} — ticket stays open for pay";
            _setStatus(PanelStatus);
        }
        catch (Exception ex)
        {
            PanelStatus = $"Send kitchen failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private async Task HoldTicketAsync()
    {
        try
        {
            if (Lines.Count == 0) throw new InvalidOperationException("Ticket is empty.");
            var label = await UiPrompt.PromptTextAsync(
                "Hold ticket — name or phone",
                "Name / phone label",
                initial: string.IsNullOrWhiteSpace(CustomerName) ? CustomerPhone : CustomerName);
            if (label is null) return;
            if (string.IsNullOrWhiteSpace(label))
            {
                PanelStatus = "Hold needs a name or phone label";
                return;
            }

            if (string.IsNullOrWhiteSpace(CustomerName))
                CustomerName = label.Trim();
            var order = PersistTicket(PosOrderStatus.Held);
            order.HoldLabel = label.Trim();
            order.UpdatedAt = DateTimeOffset.Now;
            _app.Orders.Upsert(order);
            NewTicket(force: true);
            RefreshHeldList();
            PanelStatus = $"Held: {label.Trim()}";
            _setStatus(PanelStatus);
        }
        catch (Exception ex)
        {
            PanelStatus = $"Hold failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private void ResumeHeld(PosOrder? order)
    {
        if (order is null) return;
        if (Lines.Count > 0)
        {
            PanelStatus = "Clear or pay current ticket before resuming Held";
            return;
        }
        LoadTicket(order);
        PanelStatus = $"Resumed held {order.OrderNumber} ({order.HoldLabel})";
        _setStatus(PanelStatus);
    }

    [RelayCommand]
    private async Task PayCashAsync()
    {
        // First tap opens tender pad; confirm from pad
        if (!ShowCashPanel)
        {
            BeginCashPay();
            return;
        }

        try
        {
            ValidateForSendOrPay(requireAddress: true);
            var settings = _app.GetSettings();
            var order = PersistTicket(PosOrderStatus.Paid);

            decimal tendered = order.Total;
            if (decimal.TryParse(CashTenderedText, out var t) && t > 0)
                tendered = t;
            if (tendered < order.Total)
                throw new InvalidOperationException($"Cash tendered £{tendered:0.00} < total £{order.Total:0.00}");

            order.Tenders.Add(new OrderTender
            {
                Type = TenderType.Cash,
                Amount = tendered,
                Reference = tendered > order.Total ? $"change:{(tendered - order.Total):0.00}" : "exact",
            });
            order.PaymentLabel = "CASH";
            order.Status = PosOrderStatus.Paid;
            _app.Orders.Upsert(order);

            if (settings.SendKitchenOnPay && order.HasUnsentLines)
                await _app.Print.PrintKitchenAsync(order, unsentOnly: order.Lines.Any(l => l.KitchenSent));
            else if (settings.SendKitchenOnPay && !order.KitchenPrinted)
                await _app.Print.PrintKitchenAsync(order);

            if (settings.PrintFrontOnPay)
                await _app.Print.PrintFrontAsync(order);
            if (settings.OpenDrawerOnCash)
                await _app.CashDrawer.OpenAsync();

            ChangeText = tendered > order.Total
                ? $"Change £{tendered - order.Total:0.00}"
                : "Exact";

            PanelStatus = $"Cash paid {order.OrderNumber} £{order.Total:0.00}  {ChangeText}";
            _setStatus(PanelStatus);
            ShowCashPanel = false;
            NewTicket(force: true);
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
            ValidateForSendOrPay(requireAddress: true);
            var settings = _app.GetSettings();
            var order = PersistTicket(PosOrderStatus.Paid);
            var result = await _app.CardTerminal.StartSaleAsync(order.Total);
            order.Tenders.Add(new OrderTender
            {
                Type = TenderType.CardManual,
                Amount = order.Total,
                Reference = result.Message,
            });
            order.PaymentLabel = "CARD";
            order.Status = PosOrderStatus.Paid;
            _app.Orders.Upsert(order);

            if (settings.SendKitchenOnPay && order.HasUnsentLines)
                await _app.Print.PrintKitchenAsync(order, unsentOnly: order.Lines.Any(l => l.KitchenSent));
            else if (settings.SendKitchenOnPay && !order.KitchenPrinted)
                await _app.Print.PrintKitchenAsync(order);

            if (settings.PrintFrontOnPay)
                await _app.Print.PrintFrontAsync(order);

            PanelStatus = $"Card (manual) {order.OrderNumber} £{order.Total:0.00}";
            _setStatus(PanelStatus);
            NewTicket(force: true);
        }
        catch (Exception ex)
        {
            PanelStatus = $"Card pay failed: {ex.Message}";
            _setStatus(PanelStatus);
        }
    }

    /// <summary>Open an unpaid / held order from Orders into Sell for continue-pay.</summary>
    public void LoadOrderForContinue(PosOrder order)
    {
        LoadTicket(order);
        PanelStatus = $"Opened {order.OrderNumber} · {order.Status} — continue pay or add items";
        _setStatus(PanelStatus);
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

    public void StartDeliveryForCustomer(Customer customer, CustomerAddress? address = null)
    {
        if (Lines.Count > 0 && _ticket.Status == PosOrderStatus.Draft)
        {
            // keep food, switch customer
        }
        else if (Lines.Count == 0)
            NewTicket(force: true);

        CustomerName = customer.Name;
        CustomerPhone = customer.Phone;
        var addr = address ?? customer.Addresses.FirstOrDefault(a => a.IsDefault) ?? customer.Addresses.FirstOrDefault();
        if (addr is not null)
        {
            DeliveryAddress = string.Join(", ", new[] { addr.Line1, addr.Line2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
            DeliveryPostcode = addr.Postcode;
            OrderType = "Delivery";
        }
        else
            OrderType = "Collection";
        PanelStatus = $"Phone order · {customer.Name}";
    }

    private void NewTicket(bool force = false)
    {
        _ticket = new PosOrder();
        Lines.Clear();
        SelectedLine = null;
        CustomerName = "";
        CustomerPhone = "";
        DeliveryAddress = "";
        DeliveryPostcode = "";
        TableNumber = "";
        OrderNotes = "";
        CashTenderedText = "";
        ChangeText = "";
        ShowCashPanel = false;
        ShowHeldPanel = false;
        ShowCustomerMore = false;
        OrderType = "Collection";
        SyncTicketTotals();
        RefreshHeldList();
    }

    private void LoadTicket(PosOrder order)
    {
        _ticket = order;
        Lines.Clear();
        foreach (var l in order.Lines) Lines.Add(l);
        OrderType = order.OrderType.ToString();
        CustomerName = order.CustomerName ?? "";
        CustomerPhone = order.CustomerPhone ?? "";
        DeliveryAddress = order.DeliveryAddress ?? "";
        DeliveryPostcode = order.DeliveryPostcode ?? "";
        TableNumber = order.TableNumber ?? "";
        OrderNotes = order.Notes ?? "";
        CashTenderedText = "";
        ChangeText = "";
        SelectedLine = Lines.LastOrDefault(l => !l.KitchenSent) ?? Lines.LastOrDefault();
        SyncTicketTotals();
        RefreshHeldList();
    }

    private void ValidateForSendOrPay(bool requireAddress)
    {
        if (Lines.Count == 0) throw new InvalidOperationException("Ticket is empty.");
        if (OrderType == "Delivery" && requireAddress &&
            string.IsNullOrWhiteSpace(DeliveryAddress) &&
            string.IsNullOrWhiteSpace(DeliveryPostcode))
            throw new InvalidOperationException("Delivery needs address or postcode.");
        if (OrderType == "EatIn" && string.IsNullOrWhiteSpace(TableNumber))
            throw new InvalidOperationException("TABLE needs table / pager number.");
    }

    private PosOrder PersistTicket(PosOrderStatus status)
    {
        _ticket.OrderNumber = string.IsNullOrWhiteSpace(_ticket.OrderNumber)
            ? _app.Settings.AllocateOrderNumber()
            : _ticket.OrderNumber;
        _ticket.OrderType = Enum.Parse<PosOrderType>(OrderType);
        _ticket.Source = PosOrderSource.Pos;
        // Don't downgrade Sent → Draft when only adding items
        if (status == PosOrderStatus.Sent || _ticket.Status is PosOrderStatus.Draft or PosOrderStatus.Open or PosOrderStatus.Held)
            _ticket.Status = status;
        else if (status == PosOrderStatus.Paid)
            _ticket.Status = PosOrderStatus.Paid;
        else if (status == PosOrderStatus.Held)
            _ticket.Status = PosOrderStatus.Held;

        _ticket.CustomerName = NullIfEmpty(CustomerName);
        _ticket.CustomerPhone = NullIfEmpty(CustomerPhone);
        _ticket.DeliveryAddress = NullIfEmpty(DeliveryAddress);
        _ticket.DeliveryPostcode = NullIfEmpty(DeliveryPostcode);
        _ticket.TableNumber = NullIfEmpty(TableNumber);
        if (_ticket.OrderType == PosOrderType.EatIn && !string.IsNullOrWhiteSpace(TableNumber))
            _ticket.FulfilmentLabel = $"Table {TableNumber.Trim()}";
        _ticket.Notes = NullIfEmpty(OrderNotes);
        _ticket.Lines = Lines.ToList();
        _ticket.DeliveryFee = _ticket.OrderType == PosOrderType.Delivery ? _app.GetSettings().DefaultDeliveryFee : 0;
        _ticket.UpdatedAt = DateTimeOffset.Now;
        LinePricing.RecalculateOrder(_ticket);

        if (!string.IsNullOrWhiteSpace(_ticket.CustomerPhone))
        {
            var existing = _app.Customers.FindByPhone(_ticket.CustomerPhone!);
            var c = existing ?? new Customer { Phone = _ticket.CustomerPhone! };
            if (!string.IsNullOrWhiteSpace(_ticket.CustomerName)) c.Name = _ticket.CustomerName!;
            if (!string.IsNullOrWhiteSpace(_ticket.DeliveryAddress) || !string.IsNullOrWhiteSpace(_ticket.DeliveryPostcode))
            {
                var already = c.Addresses.Any(a =>
                    string.Equals(a.Line1, _ticket.DeliveryAddress, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.Postcode, _ticket.DeliveryPostcode, StringComparison.OrdinalIgnoreCase));
                if (!already)
                {
                    c.Addresses.Add(new CustomerAddress
                    {
                        Line1 = _ticket.DeliveryAddress ?? "",
                        Postcode = _ticket.DeliveryPostcode ?? "",
                        IsDefault = c.Addresses.Count == 0,
                    });
                }
            }
            _app.Customers.Upsert(c);
            _ticket.CustomerId = c.Id;
        }

        _app.Orders.Upsert(_ticket);
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
        IsTicketEmpty = Lines.Count == 0;
        HasUnsentLines = Lines.Any(l => !l.KitchenSent);
        TicketIsSent = Lines.Any(l => l.KitchenSent) || _ticket.Status == PosOrderStatus.Sent;
        RefreshLines();
        UpdateChangePreview();
        UpdateTicketChrome();
        UpdateCustomerSummary();
    }

    private void UpdateCustomerSummary()
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(CustomerName)) bits.Add(CustomerName.Trim());
        if (!string.IsNullOrWhiteSpace(CustomerPhone)) bits.Add(CustomerPhone.Trim());
        if (IsDelivery)
        {
            if (!string.IsNullOrWhiteSpace(DeliveryPostcode)) bits.Add(DeliveryPostcode.Trim());
            else if (!string.IsNullOrWhiteSpace(DeliveryAddress)) bits.Add(DeliveryAddress.Trim());
        }
        if (IsEatIn && !string.IsNullOrWhiteSpace(TableNumber))
            bits.Add($"T{TableNumber.Trim()}");
        CustomerSummary = bits.Count == 0 ? "" : string.Join(" · ", bits);
    }

    private void UpdateTicketChrome()
    {
        var zh = _app.GetSettings().UiLanguage == "zh";
        var num = string.IsNullOrWhiteSpace(_ticket.OrderNumber) ? (zh ? "新单" : "NEW") : _ticket.OrderNumber;
        var status = _ticket.Status switch
        {
            PosOrderStatus.Sent => "SENT",
            PosOrderStatus.Held => "HELD",
            PosOrderStatus.Paid => "PAID",
            PosOrderStatus.Voided => "VOID",
            _ => Lines.Any(l => l.KitchenSent) ? "SENT" : "DRAFT",
        };
        TicketNumberText = num;
        TicketStatusBadge = status;
        TicketHeader = $"{num} · {status}";
        if (Lines.Any(l => l.KitchenSent) && Lines.Any(l => !l.KitchenSent))
            SendButtonText = zh ? "补打厨房" : "SEND NEW";
        else
            SendButtonText = zh ? "送厨" : "SEND";
    }

    partial void OnCustomerNameChanged(string value) => UpdateCustomerSummary();
    partial void OnCustomerPhoneChanged(string value) => UpdateCustomerSummary();
    partial void OnDeliveryAddressChanged(string value) => UpdateCustomerSummary();
    partial void OnDeliveryPostcodeChanged(string value) => UpdateCustomerSummary();
    partial void OnTableNumberChanged(string value) => UpdateCustomerSummary();

    private void RefreshLines()
    {
        var snap = Lines.ToList();
        var selId = SelectedLine?.Id;
        Lines.Clear();
        foreach (var l in snap) Lines.Add(l);
        if (selId is not null)
            SelectedLine = Lines.FirstOrDefault(l => l.Id == selId);
    }

    partial void OnOrderTypeChanged(string value)
    {
        IsCollection = value == "Collection";
        IsDelivery = value == "Delivery";
        IsWalkIn = value == "WalkIn";
        IsEatIn = value == "EatIn";
        if (IsDelivery || IsEatIn)
            ShowCustomerMore = true;
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
