using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

public partial class TillViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action? _goOrders;
    private PosOrder _ticket = new();
    private Dictionary<string, IReadOnlyList<string>> _pendingSelections = new();

    public TillViewModel(AppServices app, Action<string> setStatus, Action? goOrders = null)
    {
        _app = app;
        _setStatus = setStatus;
        _goOrders = goOrders;
        // Before NewTicket, which resets it.
        AddressLookup = new AddressLookupPanel(app.AddressLookup, () => DeliveryPostcode, ApplyFoundAddress);
        ReloadQuickNotes();
        RefreshMenu();
        NewTicket(force: true);
        RefreshUiLabels();
    }

    /// <summary>Postcode → address, beside the delivery fields.</summary>
    public AddressLookupPanel AddressLookup { get; }

    /// <summary>What the zone said: the fee, and anything staff should know.</summary>
    [ObservableProperty] private string _deliveryZoneNote = "";
    [ObservableProperty] private bool _hasDeliveryZoneNote;
    [ObservableProperty] private bool _deliveryNeedsAttention;

    /// <summary>Surcharge added to reach a zone's minimum, shown so it is never a surprise.</summary>
    [ObservableProperty] private decimal _belowMinimumSurcharge;
    [ObservableProperty] private bool _hasBelowMinimumSurcharge;

    /// <summary>
    /// Prices the delivery for whatever is on the ticket now.
    /// <para>
    /// Recomputed rather than remembered, because both halves move: the postcode
    /// changes when a customer is picked, and the food value changes with every
    /// dish — and "free over £20" has to notice the moment the order crosses £20.
    /// </para>
    /// </summary>
    private DeliveryQuote QuoteDelivery()
    {
        if (OrderType != "Delivery")
        {
            SetDeliveryNote(DeliveryQuote.None);
            return DeliveryQuote.None;
        }

        var settings = _app.GetSettings();

        // The basket before discounts, matching the website: it is what the
        // customer actually ordered, and a voucher must not quietly withdraw the
        // free delivery they were already shown.
        var orderValue = Money.Round(Lines.Sum(l => l.LineTotal));

        var config = new DeliveryConfig
        {
            Mode = settings.DeliveryMode,
            DefaultFee = settings.DefaultDeliveryFee,
            BelowMinimumSurcharge = settings.BelowMinimumSurcharge,
            MaxDeliveryMiles = settings.MaxDeliveryMiles,
            Zones = _app.DeliveryZones.GetZones(),
            MilesBands = _app.MilesBands.GetBands(),
        };

        var postcode = UkPostcode.Normalise(DeliveryPostcode);

        // Only a distance already paid for is used inline. Routing is a network
        // call and this runs on every keystroke and every dish; the fetch happens
        // once, in the background, when the postcode settles.
        var miles = config.Mode is DeliveryMode.Miles or DeliveryMode.Hybrid
            ? _app.MilesBands.GetCachedMiles(settings.ShopPostcode, postcode.Value)
            : null;

        var quote = DeliveryPricing.Quote(config, postcode, orderValue, miles, UiText.IsZh);

        SetDeliveryNote(quote);
        return quote;
    }

    private void SetDeliveryNote(DeliveryQuote quote)
    {
        DeliveryZoneNote = quote.Message;
        HasDeliveryZoneNote = quote.Message.Length > 0;
        DeliveryNeedsAttention = quote.NeedsAttention;
        BelowMinimumSurcharge = quote.Surcharge;
        HasBelowMinimumSurcharge = quote.Surcharge > 0;
    }


    /// <summary>
    /// A picked address overwrites both fields, and normalises the postcode on the
    /// way in — the order, the receipt and the customer record then all carry the
    /// same spelling, which is what makes the history search find it next time.
    /// </summary>
    private void ApplyFoundAddress(AddressCandidate candidate)
    {
        DeliveryAddress = candidate.StreetLine;

        var normalised = UkPostcode.Normalise(candidate.Postcode);
        if (!normalised.IsEmpty) DeliveryPostcode = normalised.Value;

        OrderType = "Delivery";
    }

    public ObservableCollection<CategoryTile> CategoryTiles { get; } = [];
    public ObservableCollection<MenuItem> Items { get; } = [];

    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private string _pageLabel = "1 / 1";
    [ObservableProperty] private bool _hasPages;
    public ObservableCollection<CartLine> Lines { get; } = [];
    public ObservableCollection<QuickNoteItem> QuickNotes { get; } = [];
    public ObservableCollection<ModifierGroupVm> ModifierGroups { get; } = [];
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
    [ObservableProperty] private decimal _discountTotal;
    [ObservableProperty] private bool _hasDiscount;
    [ObservableProperty] private string _discountText = "";
    [ObservableProperty] private string _cashTenderedText = "";
    [ObservableProperty] private string _changeText = "";
    [ObservableProperty] private string _adHocName = "";
    [ObservableProperty] private string _adHocPrice = "";
    [ObservableProperty] private string _adHocZh = "";
    [ObservableProperty] private string _modifierSummary = "";
    [ObservableProperty] private bool _showModifierPanel;
    [ObservableProperty] private string _modifierTitle = "";
    [ObservableProperty] private string _modifierError = "";
    [ObservableProperty] private string _modifierPriceText = "";
    [ObservableProperty] private string _modifierHint = "";
    [ObservableProperty] private string _lblAddToTicket = "Add to ticket";
    [ObservableProperty] private string _lblCancel = "Cancel";
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
    [ObservableProperty] private string _wmSearch = "Search dishes";
    [ObservableProperty] private string _wmDishNumber = "Menu #";
    [ObservableProperty] private string _lblAdHoc = "Custom item";
    [ObservableProperty] private string _wmAdHocName = "Item name";
    [ObservableProperty] private string _lblAdd = "Add";
    [ObservableProperty] private string _lblTypeCollection = "Collect";
    [ObservableProperty] private string _lblTypeDelivery = "Deliver";
    [ObservableProperty] private string _lblTypeWaiting = "Waiting";
    [ObservableProperty] private string _lblTypeTable = "Table";
    [ObservableProperty] private string _wmName = "Name";
    [ObservableProperty] private string _wmPhone = "Phone";
    [ObservableProperty] private string _wmAddress = "Address";
    [ObservableProperty] private string _wmPostcode = "Postcode";
    [ObservableProperty] private string _wmTable = "Table / pager #";
    [ObservableProperty] private string _lblNotes = "Notes";
    [ObservableProperty] private string _wmOrderNotes = "Order notes";
    [ObservableProperty] private string _lblCashTender = "Cash tendered";
    [ObservableProperty] private string _lblExact = "Exact";
    [ObservableProperty] private string _lblTicket = "TICKET";
    [ObservableProperty] private string _lblLineSent = "SENT";
    [ObservableProperty] private string _lblRequired = "REQ";
    [ObservableProperty] private string _itemsCountText = "0 items";
    [ObservableProperty] private string _lblClearCash = "CLR";
    [ObservableProperty] private string _lblDiscount = "Discount";
    [ObservableProperty] private string _lblUndo = "Remove line";
    [ObservableProperty] private decimal _amountPaid;
    [ObservableProperty] private decimal _balanceDue;
    [ObservableProperty] private bool _hasPayments;
    [ObservableProperty] private string _paymentSummaryText = "";
    [ObservableProperty] private string _balanceDueText = "";
    [ObservableProperty] private string _dueLabel = "Due";
    [ObservableProperty] private string _paidLabel = "Paid";
    [ObservableProperty] private string _lblPayCardOnPad = "Card (balance)";
    [ObservableProperty] private string _lblPrintInterim = "Interim receipt";
    [ObservableProperty] private string _lblTakePayment = "Take payment";
    [ObservableProperty] private string _lblOrderTotal = "Order total";
    [ObservableProperty] private string _lblTakenSoFar = "Taken so far";
    [ObservableProperty] private string _lblExactAmount = "Exact";
    [ObservableProperty] private string _lblNextTicket = "Next";
    [ObservableProperty] private bool _showChangeOverlay;
    [ObservableProperty] private string _changeOverlayText = "";
    [ObservableProperty] private string _changeOverlaySub = "";
    private bool _cashOverwriteNext;
    /// <summary>After full pay, dismiss settlement overlay starts a blank ticket.</summary>
    private bool _blankAfterSettlementDismiss;

    public void RefreshUiLabels()
    {
        LblPhoneOrder = UiText.PhoneOrder;
        LblDiscount = UiText.Pick("Discount", "折扣");
        LblUndo = UiText.Pick("Remove line", "删除该行");
        LblHeld = UiText.Held;
        LblNew = UiText.NewTicket;
        LblClear = UiText.ClearTicket;
        LblHold = UiText.Hold;
        LblPayCash = UiText.Cash;
        LblPayCard = UiText.Card;
        LblConfirmCash = UiText.TakeCash;
        LblBack = UiText.Back;
        LblAddToTicket = UiText.AddToTicket;
        LblCancel = UiText.Cancel;
        EmptyTicketHint = UiText.EmptyTicket;
        WmSearch = UiText.Search;
        WmDishNumber = UiText.DishNumber;
        LblAdHoc = UiText.AdHoc;
        WmAdHocName = UiText.AdHocName;
        LblAdd = UiText.AddByNumber;
        LblTypeCollection = UiText.TypeCollection;
        LblTypeDelivery = UiText.TypeDelivery;
        LblTypeWaiting = UiText.TypeWaiting;
        LblTypeTable = UiText.TypeTable;
        WmName = UiText.CustomerName;
        WmPhone = UiText.CustomerPhone;
        WmAddress = UiText.Address;
        WmPostcode = UiText.Postcode;
        AddressLookup.RefreshUiLabels();
        WmTable = UiText.TablePager;
        LblNotes = UiText.Notes;
        WmOrderNotes = UiText.OrderNotes;
        LblCashTender = UiText.CashTender;
        LblExact = UiText.Exact;
        LblTicket = UiText.TicketLabel;
        LblLineSent = UiText.LineSent;
        LblRequired = UiText.Pick("REQ", "必选");
        LblClearCash = UiText.ClearCash;
        DueLabel = UiText.BalanceDue;
        PaidLabel = UiText.AmountPaid;
        LblPayCardOnPad = UiText.PayCardBalance;
        LblPrintInterim = UiText.Pick("Interim receipt", "中途收据");
        LblTakePayment = UiText.Pick("Take payment", "收款");
        LblOrderTotal = UiText.Pick("Order total", "订单金额");
        LblTakenSoFar = UiText.Pick("Taken so far", "已收明细");
        LblNextTicket = UiText.Pick("Next order", "下一单");
        ItemsCountText = UiText.ItemsCount(LineCount);
        ReloadQuickNotes();
        RefreshHeldList();
        UpdateTicketChrome();
        RefreshPaymentSummary();
        UpdateCashConfirmLabel();
        if (ShowModifierPanel) RebuildModifierPanel();
    }

    public void ReloadQuickNotes()
    {
        QuickNotes.Clear();
        var notes = _app.GetSettings().QuickNotes;
        if (notes.Count == 0) notes = QuickKitchenNotes.CreateDefaultList();
        var zh = UiText.IsZh;
        foreach (var n in notes)
        {
            // Chip shows UI language; kitchen still receives EN/ZH pair on apply.
            var chip = zh
                ? (string.IsNullOrWhiteSpace(n.Zh) ? n.En : n.Zh)
                : n.En;
            QuickNotes.Add(new QuickNoteItem(n.En, n.Zh, chip));
        }
    }

    public void RefreshHeldList()
    {
        HeldOrders.Clear();
        foreach (var o in _app.Orders.GetTodayFiltered("held"))
            HeldOrders.Add(o);
        HeldCount = HeldOrders.Count;
        HeldButtonText = HeldCount > 0
            ? $"{UiText.Held} ({HeldCount})"
            : UiText.Held;
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
            PanelStatus = UiText.Pick("Ticket is empty", "订单是空的");
            return;
        }
        RefreshPaymentSummary();
        if (BalanceDue <= 0.001m)
        {
            PanelStatus = UiText.Pick("Already fully paid", "已付清");
            return;
        }
        ShowCashPanel = true;
        CashExact(); // start at remaining balance; next digit overwrites
    }

    [RelayCommand]
    private void CancelCashPay()
    {
        ShowCashPanel = false;
        CashTenderedText = "";
        ChangeText = "";
        _cashOverwriteNext = false;
        UpdateCashConfirmLabel();
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

    /// <summary>
    /// Dishes are laid out in a fixed grid and paged, never scrolled.
    /// <para>
    /// A cook's-eye reason: staff learn where a dish is and stop reading. A
    /// flow layout moves every tile after a rename, and a scroll view puts the
    /// same dish in a different place depending on where the list was left. One
    /// page, one arrangement, every time — this is the single biggest thing
    /// separating a till someone can use at speed from one they resent.
    /// </para>
    /// </summary>
    private const int ItemsPerPage = 20;   // 5 columns x 4 rows at 1366x768

    private readonly List<MenuItem> _pageSource = [];

    private void LoadItems()
    {
        _pageSource.Clear();
        _pageSource.AddRange(string.IsNullOrWhiteSpace(SearchText)
            ? _app.Menu.GetItems(SelectedCategory?.Id)
            : _app.Menu.Search(SearchText));

        PageIndex = 0;
        ApplyPage();

        if (!string.IsNullOrWhiteSpace(SearchText))
            CategoryHeading = $"Search: {SearchText}";
        else if (SelectedCategory is not null)
            CategoryHeading = SelectedCategory.Name;
    }

    private void ApplyPage()
    {
        PageCount = Math.Max(1, (int)Math.Ceiling(_pageSource.Count / (double)ItemsPerPage));
        PageIndex = Math.Clamp(PageIndex, 0, PageCount - 1);

        Items.Clear();
        foreach (var item in _pageSource.Skip(PageIndex * ItemsPerPage).Take(ItemsPerPage))
            Items.Add(item);

        HasPages = PageCount > 1;
        PageLabel = $"{PageIndex + 1} / {PageCount}";
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (PageIndex == 0) return;
        PageIndex--;
        ApplyPage();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (PageIndex >= PageCount - 1) return;
        PageIndex++;
        ApplyPage();
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
        LineNotesDraft = "";
        ModifierError = "";
        if (item.OptionGroups.Count == 0)
        {
            AddSelectedItem();
            return;
        }

        ModifierTitle = string.IsNullOrWhiteSpace(item.MenuNumber)
            ? item.Name
            : $"{item.MenuNumber}  {item.Name}";
        ModifierHint = string.IsNullOrWhiteSpace(item.Description) ? "" : item.Description;
        RebuildModifierPanel();
        ShowModifierPanel = true;
    }

    [RelayCommand]
    private void CancelModifier()
    {
        ShowModifierPanel = false;
        SelectedItem = null;
        ModifierGroups.Clear();
        ModifierSummary = "";
        ModifierError = "";
        ModifierPriceText = "";
        ModifierHint = "";
    }

    [RelayCommand]
    private void ToggleOption(ModifierChoiceVm? choice)
    {
        if (SelectedItem is null || choice is null) return;
        var group = SelectedItem.OptionGroups.FirstOrDefault(g => g.Id == choice.GroupId);
        if (group is null) return;

        var current = _pendingSelections.TryGetValue(group.Id, out var ids) ? ids.ToList() : [];
        var zh = _app.GetSettings().UiLanguage == "zh";

        if (group.Type is OptionGroupType.Single)
        {
            // Tap same radio again does nothing (keep required selection)
            _pendingSelections[group.Id] = new[] { choice.ChoiceId };
            ModifierError = "";
        }
        else
        {
            if (current.Contains(choice.ChoiceId))
            {
                current.Remove(choice.ChoiceId);
            }
            else
            {
                var max = group.MaxSelections ?? group.Choices.Count;
                if (current.Count >= max)
                {
                    ModifierError = zh
                        ? $"{group.Name}：最多选 {max} 项"
                        : $"{group.Name}: choose up to {max}";
                    RebuildModifierPanel();
                    return;
                }
                current.Add(choice.ChoiceId);
            }
            _pendingSelections[group.Id] = current;
            ModifierError = "";
        }

        PruneHiddenSelections();
        RebuildModifierPanel();
    }

    [RelayCommand]
    private void AddSelectedItem()
    {
        if (SelectedItem is null) return;
        var err = LinePricing.ValidateSelections(SelectedItem, _pendingSelections);
        if (err is not null)
        {
            ModifierError = err;
            PanelStatus = err;
            RebuildModifierPanel();
            return;
        }

        var line = LinePricing.BuildMenuLine(
            SelectedItem, TakePendingQuantity(), _pendingSelections, LineNotesDraft);
        // Resolved, not inherited: a dish that follows its category still has
        // to name a station on the line, or routing has nothing to match on.
        line.PrintClass = SelectedItem.EffectivePrintClass;
        line.TaxClassId = SelectedItem.EffectiveTaxClassId;
        Lines.Add(line);
        SelectedLine = line;
        LineNotesDraft = "";
        PanelStatus = $"Added {line.Quantity} x {line.Name} £{line.LineTotal:0.00}";
        ShowModifierPanel = false;
        ModifierGroups.Clear();
        ModifierError = "";
        SyncTicketTotals();
    }

    private void PruneHiddenSelections()
    {
        if (SelectedItem is null) return;
        var visibleIds = LinePricing.GetVisibleOptionGroups(SelectedItem, _pendingSelections)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in _pendingSelections.Keys.ToList())
        {
            if (!visibleIds.Contains(key))
                _pendingSelections.Remove(key);
        }
        // Apply defaults for newly visible required radio groups
        foreach (var g in LinePricing.GetVisibleOptionGroups(SelectedItem, _pendingSelections))
        {
            if (_pendingSelections.ContainsKey(g.Id)) continue;
            if (g.Type is not (OptionGroupType.Single)) continue;
            var def = g.Choices.FirstOrDefault(c => c.IsDefault && c.IsAvailable)
                      ?? (g.Required ? g.Choices.FirstOrDefault(c => c.IsAvailable) : null);
            if (def is not null)
                _pendingSelections[g.Id] = new[] { def.Id };
        }
    }

    private void RebuildModifierPanel()
    {
        ModifierGroups.Clear();
        if (SelectedItem is null) return;
        var zh = _app.GetSettings().UiLanguage == "zh";
        var visible = LinePricing.GetVisibleOptionGroups(SelectedItem, _pendingSelections);

        foreach (var g in visible)
        {
            _pendingSelections.TryGetValue(g.Id, out var selectedIds);
            selectedIds ??= Array.Empty<string>();
            var selectedCount = selectedIds.Count;
            var isMulti = g.Type == OptionGroupType.Multi;
            var max = g.MaxSelections ?? (isMulti ? g.Choices.Count : 1);
            var min = g.MinSelections ?? (g.Required ? 1 : 0);
            var atMax = isMulti && selectedCount >= max;

            var typeLabel = g.Type switch
            {
                OptionGroupType.Multi => zh ? "多选" : "Multi",
                OptionGroupType.Single => zh ? "下拉单选" : "Select",
                _ => zh ? "单选" : "Single",
            };

            string hint;
            if (isMulti)
            {
                hint = zh
                    ? $"已选 {selectedCount}/{max}" + (min > 0 ? $" · 至少 {min}" : " · 可选")
                    : $"{selectedCount}/{max} selected" + (min > 0 ? $" · min {min}" : " · optional");
            }
            else
            {
                hint = g.Required
                    ? (zh ? "必选一项" : "Choose one")
                    : (zh ? "可选一项" : "Optional · choose one");
            }

            var groupVm = new ModifierGroupVm
            {
                GroupId = g.Id,
                Name = g.Name,
                TypeLabel = typeLabel,
                Hint = hint,
                IsRequired = g.Required,
                IsMulti = isMulti,
                IsConditional = g.ShowWhen is not null,
                ConditionalNote = g.ShowWhen is null
                    ? ""
                    : (zh ? "附属选项（随主选项显示）" : "Follow-up options"),
            };

            foreach (var c in g.Choices.Where(x => x.IsAvailable))
            {
                var selected = selectedIds.Contains(c.Id);
                var enabled = selected || !atMax || !isMulti;
                var delta = c.PriceDelta == 0
                    ? ""
                    : (c.PriceDelta > 0 ? $"+£{c.PriceDelta:0.00}" : $"-£{Math.Abs(c.PriceDelta):0.00}");
                groupVm.Choices.Add(new ModifierChoiceVm
                {
                    GroupId = g.Id,
                    ChoiceId = c.Id,
                    Label = c.Label,
                    Translation = c.OptionTranslation ?? "",
                    PriceDeltaText = delta,
                    IsSelected = selected,
                    IsEnabled = enabled,
                    IsMulti = isMulti,
                    Glyph = isMulti
                        ? (selected ? "☑" : "☐")
                        : (selected ? "●" : "○"),
                });
            }

            ModifierGroups.Add(groupVm);
        }

        ModifierSummary = BuildModifierSummary(SelectedItem, _pendingSelections);
        var preview = LinePricing.BuildMenuLine(SelectedItem, 1, _pendingSelections, null);
        ModifierPriceText = $"£{preview.LineTotal:0.00}";
        var valid = LinePricing.ValidateSelections(SelectedItem, _pendingSelections);
        if (valid is not null && string.IsNullOrWhiteSpace(ModifierError))
            ModifierError = ""; // keep soft until Add; show count hints in groups
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
        AddressLookup.Reset();
        PanelStatus = "Phone order — enter name/phone or pick from Customers";
        _setStatus(PanelStatus);
    }

    /// <summary>
    /// Keyed entry by menu number, with an optional quantity in front:
    /// <c>88</c>, <c>3*88</c>, <c>3x88</c>. Experienced staff barely look at the
    /// tiles — they know the numbers, and typing "3x88" is one action where
    /// tapping a tile three times is three.
    /// </summary>
    /// <summary>
    /// Applies a keypress from the till's keyboard. Returns true when it was
    /// used, so the view can stop it going anywhere else.
    /// </summary>
    public bool ApplyShortcut(TillKeyResult key)
    {
        switch (key.Action)
        {
            case TillShortcut.AppendDigit:
                DishNumberText += key.Digit;
                return true;

            case TillShortcut.AppendTimes:
                // Only between a quantity and a dish number. A leading or
                // doubled separator is a slip, and swallowing it silently is
                // how "3**88" becomes a dish nobody ordered.
                if (DishNumberText.Length == 0 || DishNumberText.Contains('*')) return true;
                DishNumberText += '*';
                return true;

            case TillShortcut.Backspace:
                if (DishNumberText.Length > 0) DishNumberText = DishNumberText[..^1];
                return true;

            case TillShortcut.Commit:
                if (DishNumberText.Length == 0) return false;   // Enter elsewhere still works
                AddByDishNumberCommand.Execute(null);
                return true;

            case TillShortcut.Cancel:
                // The options panel first: it is the thing most recently opened
                // and the thing a cashier most wants out of the way.
                if (ShowModifierPanel) { CancelModifierCommand.Execute(null); return true; }
                if (DishNumberText.Length == 0) return false;
                DishNumberText = "";
                return true;

            case TillShortcut.QuantityUp:
                QtyPlusCommand.Execute(null);
                return true;

            case TillShortcut.QuantityDown:
                QtyMinusCommand.Execute(null);
                return true;

            default:
                return false;
        }
    }

    [RelayCommand]
    private void AddByDishNumber()
    {
        var entry = DishNumberText.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(entry))
        {
            PanelStatus = UiText.Pick("Enter a dish number, or 3x88 for three", "输入菜号，或 3x88 表示三份");
            return;
        }

        var quantity = 1;
        var separator = entry.IndexOfAny(['*', 'x', 'X', '×']);
        if (separator > 0)
        {
            if (!int.TryParse(entry[..separator], out quantity) || quantity < 1)
            {
                PanelStatus = UiText.Pick($"'{entry[..separator]}' is not a quantity", $"'{entry[..separator]}' 不是数量");
                return;
            }
            quantity = Math.Min(quantity, 99);
            entry = entry[(separator + 1)..].Trim();
        }

        var item = _app.Menu.FindByMenuNumber(entry);
        if (item is null)
        {
            PanelStatus = UiText.Pick($"No dish #{entry}", $"没有 #{entry} 号菜");
            return;
        }

        DishNumberText = "";
        PendingQuantity = quantity;
        SelectItem(item);
    }

    /// <summary>
    /// Take money off the whole ticket. One box accepts either form because
    /// that is how it is asked for at the counter: "a fiver off" is <c>5</c>,
    /// "ten percent" is <c>10%</c>.
    /// </summary>
    [RelayCommand]
    private async Task ApplyDiscountAsync()
    {
        if (Lines.Count == 0)
        {
            PanelStatus = UiText.Pick("Add dishes before discounting", "先加菜再打折");
            return;
        }

        if (!await UiPrompt.RequireAsync(_app, Permission.Discount, UiText.Pick("Discount", "折扣")))
            return;

        var entered = await UiPrompt.PromptTextAsync(
            UiText.Pick("Discount", "折扣"),
            UiText.Pick("5 for £5 off, or 10% ", "5 表示减 £5，10% 表示打折"));
        if (string.IsNullOrWhiteSpace(entered)) return;

        var raw = entered.Trim();
        var isPercent = raw.EndsWith('%');
        if (!decimal.TryParse(raw.TrimEnd('%').Trim(), out var value) || value <= 0)
        {
            PanelStatus = UiText.Pick($"'{raw}' is not a discount", $"'{raw}' 不是有效折扣");
            return;
        }

        var goods = Money.Round(Lines.Sum(l => l.LineTotal));
        var amount = isPercent ? Money.Round(goods * value / 100m) : Money.Round(value);

        if (amount > goods)
        {
            PanelStatus = UiText.Pick(
                $"£{amount:0.00} is more than the £{goods:0.00} of food",
                $"£{amount:0.00} 超过了菜品金额 £{goods:0.00}");
            return;
        }

        // A discount with no reason is an unexplained hole in the takings.
        var reason = await UiPrompt.PromptTextAsync(
            UiText.Pick("Reason", "折扣原因"),
            UiText.Pick("Regular customer, complaint, staff meal…", "老顾客、投诉补偿、员工餐…"));
        if (string.IsNullOrWhiteSpace(reason)) return;

        _ticket.DiscountTotal = amount;
        _ticket.DiscountReason = reason.Trim();
        SyncTicketTotals();

        _app.Session.Record("order.discount", _ticket.Id,
            $"{(isPercent ? $"{value}%" : "")} £{amount:0.00} — {reason.Trim()}");
        PanelStatus = UiText.Pick(
            $"Discount £{amount:0.00} — {reason.Trim()}",
            $"折扣 £{amount:0.00} — {reason.Trim()}");
    }

    [RelayCommand]
    private async Task ClearDiscountAsync()
    {
        if (_ticket.DiscountTotal <= 0) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.Discount, UiText.Pick("Remove discount", "取消折扣")))
            return;

        _app.Session.Record("order.discount.clear", _ticket.Id, $"was £{_ticket.DiscountTotal:0.00}");
        _ticket.DiscountTotal = 0;
        _ticket.DiscountReason = null;
        SyncTicketTotals();
        PanelStatus = UiText.Pick("Discount removed", "折扣已取消");
    }

    /// <summary>Quantity for the next dish added, from keyed entry. Resets after use.</summary>
    private int PendingQuantity { get; set; } = 1;

    private int TakePendingQuantity()
    {
        var quantity = PendingQuantity;
        PendingQuantity = 1;
        return quantity;
    }

    [RelayCommand]
    private void CashDigit(string? digit)
    {
        if (string.IsNullOrEmpty(digit)) return;
        if (_cashOverwriteNext)
        {
            CashTenderedText = digit == "." ? "0." : digit;
            _cashOverwriteNext = false;
            UpdateChangePreview();
            return;
        }
        if (digit == "." && CashTenderedText.Contains('.')) return;
        // Avoid leading junk like "12.501" after Exact — cap decimals at 2
        if (CashTenderedText.Contains('.'))
        {
            var dec = CashTenderedText.Split('.')[1];
            if (digit != "." && dec.Length >= 2) return;
        }
        CashTenderedText += digit;
        UpdateChangePreview();
    }

    [RelayCommand]
    private void CashBackspace()
    {
        _cashOverwriteNext = false;
        if (string.IsNullOrEmpty(CashTenderedText)) return;
        CashTenderedText = CashTenderedText[..^1];
        UpdateChangePreview();
    }

    [RelayCommand]
    private void CashClear()
    {
        CashTenderedText = "";
        ChangeText = "";
        _cashOverwriteNext = true;
        UpdateCashConfirmLabel();
    }

    [RelayCommand]
    private void CashExact()
    {
        RefreshPaymentSummary();
        CashTenderedText = BalanceDue.ToString("0.00");
        _cashOverwriteNext = true;
        ChangeText = UiText.Pick("Exact", "刚好");
        UpdateCashConfirmLabel();
    }

    [RelayCommand]
    private void CashQuick(string? amount)
    {
        if (amount is null) return;
        CashTenderedText = amount;
        _cashOverwriteNext = true;
        UpdateChangePreview();
    }

    private void UpdateChangePreview()
    {
        RefreshPaymentSummary();
        if (!decimal.TryParse(CashTenderedText, out var tendered) || tendered <= 0)
        {
            ChangeText = "";
            UpdateCashConfirmLabel();
            return;
        }

        if (tendered >= BalanceDue && BalanceDue > 0)
            ChangeText = tendered > BalanceDue
                ? $"{UiText.Change} £{tendered - BalanceDue:0.00}"
                : UiText.Pick("Exact", "刚好");
        else if (tendered > 0 && tendered < BalanceDue)
            ChangeText = UiText.Pick($"Then due £{BalanceDue - tendered:0.00}", $"还需 £{BalanceDue - tendered:0.00}");
        else
            ChangeText = "";
        UpdateCashConfirmLabel();
    }

    private void UpdateCashConfirmLabel()
    {
        // Both buttons name the amount they are about to act on. "Take cash" and
        // "Card" alone make the cashier check the figure somewhere else first,
        // and the one time they do not is the one that goes wrong.
        LblExactAmount = $"{UiText.Exact} £{BalanceDue:0.00}";
        LblPayCardOnPad = $"{UiText.Card} £{BalanceDue:0.00}";

        if (!decimal.TryParse(CashTenderedText, out var tendered) || tendered <= 0)
        {
            LblConfirmCash = UiText.TakeCashFull;
            return;
        }
        LblConfirmCash = tendered + 0.001m < BalanceDue
            ? $"{UiText.PayPartial} £{tendered:0.00}"
            : $"{UiText.TakeCashFull} £{tendered:0.00}";
    }

    private void RefreshPaymentSummary()
    {
        // Keep tenders on live ticket; totals from current lines
        AmountPaid = _ticket.Tenders.Sum(t => t.Amount);
        BalanceDue = Math.Max(0, Math.Round(Total - AmountPaid, 2));
        HasPayments = AmountPaid > 0;
        BalanceDueText = $"£{BalanceDue:0.00}";
        PaymentSummaryText = HasPayments
            ? $"{UiText.AmountPaid} £{AmountPaid:0.00} · {UiText.BalanceDue} £{BalanceDue:0.00}"
            : $"{UiText.BalanceDue} £{BalanceDue:0.00}";
    }

    [RelayCommand]
    private async Task EightySixItemAsync(MenuItem? item)
    {
        if (item is null) return;
        if (!await UiPrompt.RequireAsync(_app, Permission.EditMenu, UiText.Pick("Mark sold out (86)", "标记售罄 (86)")))
            return;
        item.IsAvailable = false;
        _app.Menu.SetItemAvailable(item.Id, false);
        LoadItems();
        PanelStatus = $"86 {item.MenuNumber} {item.Name}";
        _setStatus(PanelStatus);
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
            RefreshPaymentSummary();
            string msg;
            if (_ticket.Status == PosOrderStatus.Draft)
                msg = UiText.Pick("Draft not sent — clearing will lose these dishes.", "草稿未送厨 — 清空会丢失这些菜。");
            else if (HasPayments && BalanceDue > 0.001m)
                msg = UiText.Pick(
                    $"Still due £{BalanceDue:0.00}. Ticket stays in Orders for continue pay.",
                    $"仍待收 £{BalanceDue:0.00}。订单保留在订单列表可续收。");
            else if (_ticket.Status is PosOrderStatus.Sent or PosOrderStatus.Held)
                msg = UiText.Pick("Sent/held ticket stays in Orders. Clear the till screen?", "已送厨/挂单保留在订单。清空点单屏？");
            else
                msg = UiText.Pick("Clear current ticket?", "清空当前订单？");

            if (!await UiPrompt.ConfirmAsync(UiText.Pick("Clear ticket?", "清空订单？"), msg))
                return;
        }
        NewTicket(force: true);
        PanelStatus = UiText.Pick("Ticket cleared", "已清空");
    }

    [RelayCommand]
    private async Task NewTicketAsync()
    {
        RefreshPaymentSummary();
        if (Lines.Count > 0 || HasPayments)
        {
            string msg;
            if (_ticket.Status == PosOrderStatus.Draft)
                msg = UiText.Pick("Unsent draft will be lost. Start blank ticket?", "未送厨草稿会丢失。开新单？");
            else if (HasPayments && BalanceDue > 0.001m)
                msg = UiText.Pick(
                    $"Still due £{BalanceDue:0.00} — find it under Orders → Unpaid. Start blank?",
                    $"仍待收 £{BalanceDue:0.00} — 在订单→未付中续收。开新单？");
            else
                msg = UiText.Pick("Current ticket stays in Orders if already sent. Start blank?", "已送厨单保留在订单。开新单？");

            if (!await UiPrompt.ConfirmAsync(UiText.Pick("New ticket?", "新单？"), msg))
                return;
        }
        NewTicket(force: true);
        PanelStatus = UiText.Pick("New ticket", "新单");
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
            UpdatePaymentLabel(order);
            _app.Orders.Upsert(order);
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
        HideSettlementUi();
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
            RefreshPaymentSummary();
            if (BalanceDue <= 0.001m)
                throw new InvalidOperationException(UiText.Pick("Already fully paid", "已付清"));

            if (!decimal.TryParse(CashTenderedText, out var received) || received <= 0)
                throw new InvalidOperationException(UiText.Pick("Enter cash amount", "请输入现金金额"));

            var applied = Math.Min(received, BalanceDue);
            var change = Math.Max(0, Math.Round(received - BalanceDue, 2));
            var settings = _app.GetSettings();

            // Persist lines first (keep open until fully paid)
            var keepStatus = _ticket.Status is PosOrderStatus.Sent or PosOrderStatus.Held
                ? PosOrderStatus.Sent
                : PosOrderStatus.Open;
            var order = PersistTicket(keepStatus);

            var cash = new OrderTender
            {
                Type = TenderType.Cash,
                Amount = applied,
                CashReceived = received,
                ChangeGiven = change > 0 ? change : null,
                Reference = change > 0 ? $"change:{change:0.00}" : "tender",
            };
            _app.Session.Stamp(cash);
            order.Tenders.Add(cash);
            var fullyPaid = order.IsFullyPaid;
            order.Status = fullyPaid ? PosOrderStatus.Paid : PosOrderStatus.Sent;
            UpdatePaymentLabel(order);
            _app.Orders.Upsert(order);
            _ticket = order;

            try { await MaybePrintKitchenOnPayAsync(order, fullyPaid); }
            catch (Exception printEx)
            {
                _setStatus(UiText.Pick($"Paid OK — kitchen print failed: {printEx.Message}",
                    $"已收款 — 厨房打印失败: {printEx.Message}"));
            }

            if (settings.OpenDrawerOnCash)
            {
                try { await _app.Print.OpenDrawerAsync(); }
                catch { /* optional */ }
            }

            if (fullyPaid)
            {
                try
                {
                    if (settings.PrintFrontOnPay)
                        await _app.Print.PrintReceiptAsync(order);
                }
                catch (Exception printEx)
                {
                    _setStatus(UiText.Pick($"Paid OK — receipt failed: {printEx.Message}",
                        $"已付清 — 小票失败: {printEx.Message}"));
                }

                ShowCashPanel = false;
                // Keep paid ticket visible under overlay; blank only on "Next order"
                LoadTicket(order);
                PresentSettlementOverlay(
                    change > 0
                        ? $"{UiText.Change}\n£{change:0.00}"
                        : UiText.Pick("Exact — no change", "刚好 — 无找零"),
                    UiText.Pick(
                        $"Paid {order.OrderNumber} · £{order.Total:0.00}",
                        $"已付清 {order.OrderNumber} · £{order.Total:0.00}"),
                    startBlankOnDismiss: true);
                PanelStatus = ChangeOverlaySub;
                _setStatus(PanelStatus);
            }
            else
            {
                PanelStatus = UiText.Pick(
                    $"Partial £{applied:0.00} · due £{order.BalanceDue:0.00} — no final receipt",
                    $"已收 £{applied:0.00} · 待收 £{order.BalanceDue:0.00} — 未打最终小票");
                _setStatus(PanelStatus);
                LoadTicket(order);
                ShowCashPanel = true;
                CashExact();
            }
        }
        catch (Exception ex)
        {
            PanelStatus = UiText.Pick($"Cash pay failed: {ex.Message}", $"现金收款失败: {ex.Message}");
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private async Task PayCardAsync()
    {
        try
        {
            ValidateForSendOrPay(requireAddress: true);
            RefreshPaymentSummary();
            if (BalanceDue <= 0.001m)
                throw new InvalidOperationException(UiText.Pick("Already fully paid", "已付清"));

            var settings = _app.GetSettings();
            var payAmount = BalanceDue; // card settles remaining balance (split: cash first, then card)
            var keepStatus = _ticket.Status is PosOrderStatus.Sent or PosOrderStatus.Held
                ? PosOrderStatus.Sent
                : PosOrderStatus.Open;
            var order = PersistTicket(keepStatus);

            // The tender is built first so its id can be the terminal's
            // reference. A reference the till chose is what makes a lost answer
            // recoverable — see IPaymentTerminal.
            var card = new OrderTender
            {
                Type = _app.CardTerminal is ManualCardTerminal
                    ? TenderType.CardManual
                    : TenderType.CardIntegrated,
                Amount = payAmount,
            };

            var result = await _app.CardTerminal.StartAsync(new PaymentRequest
            {
                Reference = card.Id,
                Amount = payAmount,
                OrderNumber = order.OrderNumber,
            });

            // A lost answer is not a decline. Ask the terminal what it did with
            // the reference rather than retrying, because a retry is how a
            // customer gets charged twice.
            var wasRecovered = false;
            if (result.NeedsResolving)
            {
                _setStatus(UiText.Pick("No answer from the card machine — checking",
                    "刷卡机无响应 — 正在查询"));

                result = await _app.CardTerminal.QueryAsync(card.Id);
                wasRecovered = true;

                // Written down rather than left on a status line that the next
                // action overwrites. A sale that needed the terminal to be asked
                // what it had done is exactly what someone reconstructs later,
                // and by then the screen has moved on.
                _app.Session.Record("payment.recovered", order.Id,
                    $"{order.OrderNumber} £{payAmount:0.00} ref {card.Id[..8]} — {result.Outcome}");
                AppLog.Warn("payment",
                    $"lost answer on {order.OrderNumber} ref {card.Id[..8]}; terminal says {result.Outcome}");
            }

            if (result.NeedsResolving)
            {
                // Still unknown. Neither taking the money nor releasing the
                // ticket is safe, so the till does neither and says so.
                await UiPrompt.AlertAsync(
                    UiText.Pick("Check the card machine", "请检查刷卡机"),
                    UiText.Pick(
                        $"The card machine did not answer for £{payAmount:0.00}.\n\n" +
                        $"Reference {card.Id[..8]}\n\n" +
                        "Check the machine before taking payment again — the customer may already have been charged.",
                        $"刷卡机未对 £{payAmount:0.00} 作出响应。\n\n" +
                        $"参考号 {card.Id[..8]}\n\n" +
                        "再次收款前请先检查刷卡机 — 顾客可能已经被扣款。"));

                _setStatus(UiText.Pick("Card unresolved — ticket left unpaid",
                    "刷卡状态未知 — 账单保持未付"));
                return;
            }

            if (!result.IsApproved)
                throw new InvalidOperationException(
                    result.Message ?? UiText.Pick("Card declined", "刷卡失败"));

            // A terminal may authorise less than was asked for.
            if (result.AmountAuthorised > 0 && result.AmountAuthorised < payAmount)
            {
                card.Amount = result.AmountAuthorised;
                payAmount = result.AmountAuthorised;
            }

            // The reference goes on the tender, and a recovered payment says so,
            // because "this one had to be chased" is the first thing anyone
            // wants to know when a card sale is queried weeks later.
            card.Reference = wasRecovered
                ? $"{result.AuthCode ?? "card"} (recovered)"
                : result.AuthCode ?? result.Message ?? "card";

            if (wasRecovered)
                _setStatus(UiText.Pick(
                    $"Card recovered — the machine had taken £{payAmount:0.00}",
                    $"已向刷卡机确认 — £{payAmount:0.00} 实际已扣款"));
            _app.Session.Stamp(card);
            order.Tenders.Add(card);
            var fullyPaid = order.IsFullyPaid;
            order.Status = fullyPaid ? PosOrderStatus.Paid : PosOrderStatus.Sent;
            UpdatePaymentLabel(order);
            _app.Orders.Upsert(order);
            _ticket = order;

            try { await MaybePrintKitchenOnPayAsync(order, fullyPaid); }
            catch (Exception printEx)
            {
                _setStatus(UiText.Pick($"Card OK — kitchen print failed: {printEx.Message}",
                    $"刷卡成功 — 厨房打印失败: {printEx.Message}"));
            }

            if (fullyPaid)
            {
                try
                {
                    if (settings.PrintFrontOnPay)
                        await _app.Print.PrintReceiptAsync(order);
                }
                catch (Exception printEx)
                {
                    _setStatus(UiText.Pick($"Card OK — receipt failed: {printEx.Message}",
                        $"刷卡成功 — 小票失败: {printEx.Message}"));
                }

                PanelStatus = UiText.Pick(
                    $"Card paid {order.OrderNumber} £{payAmount:0.00}",
                    $"刷卡付清 {order.OrderNumber} £{payAmount:0.00}");
                _setStatus(PanelStatus);
                ShowCashPanel = false;
                LoadTicket(order);
                PresentSettlementOverlay(
                    UiText.Pick("CARD PAID", "刷卡已付"),
                    PanelStatus,
                    startBlankOnDismiss: true);
            }
            else
            {
                PanelStatus = UiText.Pick(
                    $"Card £{payAmount:0.00} · due £{order.BalanceDue:0.00} — no final receipt",
                    $"刷卡 £{payAmount:0.00} · 待收 £{order.BalanceDue:0.00} — 未打最终小票");
                _setStatus(PanelStatus);
                LoadTicket(order);
                ShowCashPanel = false;
            }
        }
        catch (Exception ex)
        {
            PanelStatus = UiText.Pick($"Card pay failed: {ex.Message}", $"刷卡失败: {ex.Message}");
            _setStatus(PanelStatus);
        }
    }

    private static void UpdatePaymentLabel(PosOrder order)
    {
        if (!order.IsFullyPaid)
        {
            order.PaymentLabel = order.HasPayments
                ? $"PART PAID DUE £{order.BalanceDue:0.00}"
                : "UNPAID";
            return;
        }

        var types = order.Tenders.Select(t => t.Type).Distinct().ToList();
        if (types.Count > 1)
            order.PaymentLabel = "SPLIT";
        else if (types.Count == 1 && types[0] == TenderType.Cash)
            order.PaymentLabel = "CASH";
        else if (types.Count == 1 && types[0] == TenderType.CardManual)
            order.PaymentLabel = "CARD";
        else if (types.Count == 1)
            order.PaymentLabel = types[0].ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Kitchen on pay: only new unsent lines, or full pay of a never-printed ticket.
    /// Partial pay on an already-sent ticket must not reprint kitchen.
    /// </summary>
    private async Task MaybePrintKitchenOnPayAsync(PosOrder order, bool fullyPaid)
    {
        var settings = _app.GetSettings();
        if (!settings.SendKitchenOnPay) return;

        if (order.HasUnsentLines)
        {
            var unsentOnly = order.Lines.Any(l => l.KitchenSent);
            await _app.Print.PrintKitchenAsync(order, unsentOnly: unsentOnly);
            return;
        }

        if (fullyPaid && !order.KitchenPrinted)
            await _app.Print.PrintKitchenAsync(order);
    }

    [RelayCommand]
    private async Task PrintInterimReceiptAsync()
    {
        try
        {
            RefreshPaymentSummary();
            if (!HasPayments || BalanceDue <= 0.001m)
            {
                PanelStatus = UiText.Pick("Interim receipt only after partial payment", "仅部分付款后可打中途收据");
                return;
            }
            var order = PersistTicket(_ticket.Status is PosOrderStatus.Sent ? PosOrderStatus.Sent : PosOrderStatus.Open);
            UpdatePaymentLabel(order);
            _app.Orders.Upsert(order);
            await _app.Print.PrintReceiptAsync(order);
            PanelStatus = UiText.Pick(
                $"Interim receipt · due £{order.BalanceDue:0.00}",
                $"中途收据 · 待收 £{order.BalanceDue:0.00}");
            _setStatus(PanelStatus);
        }
        catch (Exception ex)
        {
            PanelStatus = UiText.Pick($"Print failed: {ex.Message}", $"打印失败: {ex.Message}");
            _setStatus(PanelStatus);
        }
    }

    [RelayCommand]
    private void DismissChangeOverlay()
    {
        var startBlank = _blankAfterSettlementDismiss;
        HideSettlementUi();
        if (startBlank)
            NewTicket(force: true);
    }

    /// <summary>
    /// If staff leave the till while the paid/change screen is up, finish settlement
    /// so they never return to a stuck Paid overlay.
    /// </summary>
    public void CompletePendingSettlement()
    {
        if (!ShowChangeOverlay) return;
        var startBlank = _blankAfterSettlementDismiss;
        HideSettlementUi();
        if (startBlank)
            NewTicket(force: true);
    }

    private void PresentSettlementOverlay(string title, string subtitle, bool startBlankOnDismiss)
    {
        ChangeOverlayText = title;
        ChangeOverlaySub = subtitle;
        _blankAfterSettlementDismiss = startBlankOnDismiss;
        ShowChangeOverlay = true;
    }

    private void HideSettlementUi()
    {
        ShowChangeOverlay = false;
        ChangeOverlayText = "";
        ChangeOverlaySub = "";
        _blankAfterSettlementDismiss = false;
    }

    /// <summary>Open an unpaid / held / reopened order from Orders into Till.</summary>
    public void LoadOrderForContinue(PosOrder order)
    {
        // Switching tickets ends any settlement overlay without wiping this order
        HideSettlementUi();
        LoadTicket(order);
        var dueNote = order.BalanceDue > 0.001m
            ? UiText.Pick($" · due £{order.BalanceDue:0.00}", $" · 待收 £{order.BalanceDue:0.00}")
            : UiText.Pick(" · fully paid — add dishes for new balance", " · 已付清 — 加菜后产生新待收");
        PanelStatus = UiText.Pick(
            $"Opened {order.OrderNumber}{dueNote}",
            $"已打开 {order.OrderNumber}{dueNote}");
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
        HideSettlementUi();
        _ticket = new PosOrder();
        Lines.Clear();
        SelectedLine = null;
        CustomerName = "";
        CustomerPhone = "";
        DeliveryAddress = "";
        DeliveryPostcode = "";
        AddressLookup.Reset();
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
        // Do not call HideSettlementUi here — full-pay uses LoadTicket under the overlay.
        // Callers that switch context (Orders open / New / Clear / Resume) hide first.
        _ticket = order;
        Lines.Clear();
        foreach (var l in order.Lines) Lines.Add(l);
        OrderType = TicketTypeKey(order);
        CustomerName = order.CustomerName ?? "";
        CustomerPhone = order.CustomerPhone ?? "";
        DeliveryAddress = order.DeliveryAddress ?? "";
        DeliveryPostcode = order.DeliveryPostcode ?? "";
        TableNumber = order.TableNumber ?? "";
        OrderNotes = order.Notes ?? "";
        CashTenderedText = "";
        ChangeText = "";
        ShowCashPanel = false;
        SelectedLine = Lines.LastOrDefault(l => !l.KitchenSent) ?? Lines.LastOrDefault();
        SyncTicketTotals();
        RefreshHeldList();
        RefreshPaymentSummary();
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

    /// <summary>
    /// Which service button an existing order should light up. Collection with
    /// the customer waiting maps back onto the Waiting button.
    /// </summary>
    private static string TicketTypeKey(PosOrder order) => order.ServiceType switch
    {
        ServiceType.Delivery => "Delivery",
        ServiceType.EatIn => "EatIn",
        _ => order.CustomerWaiting ? "WalkIn" : "Collection",
    };

    private PosOrder PersistTicket(PosOrderStatus status)
    {
        _ticket.OrderNumber = string.IsNullOrWhiteSpace(_ticket.OrderNumber)
            ? _app.Settings.AllocateOrderNumber()
            : _ticket.OrderNumber;
        // "Waiting" is a collection order with the customer at the counter,
        // not a fourth service type. The channel is left as it was found so
        // paying off a web order does not relabel where it came from.
        _ticket.ServiceType = OrderType switch
        {
            "Delivery" => ServiceType.Delivery,
            "EatIn" => ServiceType.EatIn,
            _ => ServiceType.Collection,
        };
        _ticket.CustomerWaiting = OrderType == "WalkIn";
        // Stamped once, on first save: reopening yesterday's ticket must not
        // move its money into today's shift.
        _app.Session.Stamp(_ticket);
        // Status transitions — never silently wipe Paid→Draft; never downgrade Sent→Draft/Open
        if (status == PosOrderStatus.Paid)
            _ticket.Status = PosOrderStatus.Paid;
        else if (status == PosOrderStatus.Held)
            _ticket.Status = PosOrderStatus.Held;
        else if (status == PosOrderStatus.Sent)
            _ticket.Status = PosOrderStatus.Sent;
        else if (status == PosOrderStatus.Open)
        {
            if (_ticket.Status is PosOrderStatus.Draft or PosOrderStatus.Open or PosOrderStatus.Held)
                _ticket.Status = PosOrderStatus.Open;
            // keep Sent if already sent
        }
        else if (_ticket.Status is PosOrderStatus.Draft or PosOrderStatus.Open or PosOrderStatus.Held)
            _ticket.Status = status;

        _ticket.CustomerName = NullIfEmpty(CustomerName);
        _ticket.CustomerPhone = NullIfEmpty(CustomerPhone);
        _ticket.DeliveryAddress = NullIfEmpty(DeliveryAddress);
        _ticket.DeliveryPostcode = NullIfEmpty(DeliveryPostcode);
        _ticket.TableNumber = NullIfEmpty(TableNumber);
        if (_ticket.ServiceType == ServiceType.EatIn && !string.IsNullOrWhiteSpace(TableNumber))
            _ticket.FulfilmentLabel = $"Table {TableNumber.Trim()}";
        _ticket.Notes = NullIfEmpty(OrderNotes);
        _ticket.Lines = Lines.ToList();

        var quote = QuoteDelivery();
        _ticket.DeliveryFee = quote.Fee;
        _ticket.BelowMinimumSurcharge = quote.Surcharge;
        _ticket.UpdatedAt = DateTimeOffset.Now;
        LinePricing.RecalculateOrder(_ticket);

        if (!string.IsNullOrWhiteSpace(_ticket.CustomerPhone))
        {
            var existing = _app.Customers.FindByPhone(_ticket.CustomerPhone!);
            var c = existing ?? new Customer { Phone = _ticket.CustomerPhone! };
            if (!string.IsNullOrWhiteSpace(_ticket.CustomerName)) c.Name = _ticket.CustomerName!;

            _app.Customers.Upsert(c);
            _ticket.CustomerId = c.Id;

            // The repository decides whether this is a door the shop already
            // knows; a near-miss on spelling used to add a second copy. When the
            // address came from a lookup it is filed as one, with the
            // coordinates, so delivery pricing never has to ask again.
            var found = AddressLookup.StillMatches(_ticket.DeliveryAddress);

            _app.Customers.SaveAddress(
                c,
                _ticket.DeliveryAddress,
                line2: null,
                town: found ? AddressLookup.LastPicked!.Town : null,
                _ticket.DeliveryPostcode,
                found ? AddressSource.Lookup : AddressSource.Manual,
                latitude: found ? AddressLookup.LastLatitude : null,
                longitude: found ? AddressLookup.LastLongitude : null);

            _app.Customers.RecordOrder(c.Id, DateTimeOffset.Now);
        }

        _app.Orders.Upsert(_ticket);
        return _ticket;
    }

    private void SyncTicketTotals()
    {
        var quote = QuoteDelivery();

        var tmp = new PosOrder
        {
            Lines = Lines.ToList(),
            DeliveryFee = quote.Fee,
            BelowMinimumSurcharge = quote.Surcharge,
            DiscountTotal = _ticket.DiscountTotal,
        };
        LinePricing.RecalculateOrder(tmp);
        Subtotal = tmp.Subtotal;
        DeliveryFee = tmp.DeliveryFee;
        Total = tmp.Total;

        // A discount stays visible while the ticket is open: staff need to see
        // it is still on before they take the money, not discover it after.
        DiscountTotal = _ticket.DiscountTotal;
        HasDiscount = DiscountTotal > 0;
        DiscountText = HasDiscount
            ? $"- £{DiscountTotal:0.00}  {_ticket.DiscountReason}".TrimEnd()
            : "";
        LineCount = Lines.Sum(l => l.Quantity);
        IsTicketEmpty = Lines.Count == 0;
        HasUnsentLines = Lines.Any(l => !l.KitchenSent);
        TicketIsSent = Lines.Any(l => l.KitchenSent) || _ticket.Status == PosOrderStatus.Sent;
        RefreshLines();
        RefreshPaymentSummary();
        UpdateTicketChrome();
        if (ShowCashPanel) UpdateChangePreview();
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
        var num = string.IsNullOrWhiteSpace(_ticket.OrderNumber) ? UiText.NewTicket : _ticket.OrderNumber;
        string status;
        if (_ticket.Status == PosOrderStatus.Paid) status = UiText.StatusPaid;
        else if (_ticket.Status == PosOrderStatus.Held) status = UiText.StatusHeld;
        else if (_ticket.Status == PosOrderStatus.Voided) status = UiText.StatusVoid;
        else if (HasPayments && BalanceDue > 0.001m) status = UiText.Pick("DUE", "待收");
        else if (HasPayments && BalanceDue <= 0.001m && _ticket.Status == PosOrderStatus.Sent)
            status = UiText.Pick("REOPEN", "重开");
        else if (_ticket.Status == PosOrderStatus.Sent || Lines.Any(l => l.KitchenSent)) status = UiText.StatusSent;
        else status = UiText.StatusDraft;
        TicketNumberText = num;
        TicketStatusBadge = status;
        TicketHeader = $"{num} · {status}";
        SendButtonText = Lines.Any(l => l.KitchenSent) && Lines.Any(l => !l.KitchenSent)
            ? UiText.SendNew
            : UiText.SendKitchen;
        ItemsCountText = UiText.ItemsCount(LineCount);
    }

    partial void OnCustomerNameChanged(string value) => UpdateCustomerSummary();
    partial void OnCustomerPhoneChanged(string value) => UpdateCustomerSummary();
    partial void OnDeliveryAddressChanged(string value) => UpdateCustomerSummary();
    /// <summary>A new postcode is a new zone, so the fee is re-quoted with it.</summary>
    partial void OnDeliveryPostcodeChanged(string value)
    {
        UpdateCustomerSummary();
        SyncTicketTotals();
        _ = FetchDistanceIfNeededAsync(value);
    }

    /// <summary>
    /// Routes the postcode once, in the background, and re-prices when the answer
    /// lands.
    /// <para>
    /// Never on the typing path: routing is a network call, and a till that
    /// stalls between keystrokes while somebody is on the phone is worse than one
    /// that shows the fee a second late. The result is cached forever, so this
    /// happens once per postcode for the life of the shop.
    /// </para>
    /// </summary>
    private async Task FetchDistanceIfNeededAsync(string rawPostcode)
    {
        var settings = _app.GetSettings();
        if (settings.DeliveryMode is DeliveryMode.Postcode) return;

        var postcode = UkPostcode.Normalise(rawPostcode);
        if (!postcode.IsValid || string.IsNullOrWhiteSpace(settings.ShopPostcode)) return;
        if (_app.MilesBands.GetCachedMiles(settings.ShopPostcode, postcode.Value) is not null) return;

        var miles = await _app.RoadDistance.MilesBetweenAsync(settings.ShopPostcode, postcode.Value);
        if (miles is null) return;

        _app.MilesBands.PutCachedMiles(settings.ShopPostcode, postcode.Value, miles.Value);

        // The postcode may have moved on while the router was thinking.
        if (UkPostcode.Normalise(DeliveryPostcode).Value == postcode.Value)
            SyncTicketTotals();
    }
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

public sealed record QuickNoteItem(string En, string Zh, string ChipLabel)
{
    public string Display => string.IsNullOrWhiteSpace(Zh) ? En : $"{En} / {Zh}";
}

public sealed class ModifierGroupVm
{
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeLabel { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool IsRequired { get; set; }
    public bool IsMulti { get; set; }
    public bool IsConditional { get; set; }
    public string ConditionalNote { get; set; } = "";
    public ObservableCollection<ModifierChoiceVm> Choices { get; } = [];
}

public sealed class ModifierChoiceVm
{
    public string GroupId { get; set; } = "";
    public string ChoiceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Translation { get; set; } = "";
    public string PriceDeltaText { get; set; } = "";
    public bool IsSelected { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsMulti { get; set; }
    public string Glyph { get; set; } = "○";
}

public sealed record CategoryTile(Category Category, bool IsSelected)
{
    public string Name => Category.Name;
}
