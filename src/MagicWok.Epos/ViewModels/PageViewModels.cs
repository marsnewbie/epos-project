using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicWok.Epos.Domain;
using MagicWok.Epos.Online;
using MagicWok.Epos.Services;

namespace MagicWok.Epos.ViewModels;

public partial class OrdersViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action<PosOrder>? _openOnSell;

    public OrdersViewModel(AppServices app, Action<string> setStatus, Action<PosOrder>? openOnSell = null)
    {
        _app = app;
        _setStatus = setStatus;
        _openOnSell = openOnSell;
    }

    public ObservableCollection<PosOrder> Orders { get; } = [];

    [ObservableProperty] private PosOrder? _selectedOrder;
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _filterAll = true;
    [ObservableProperty] private bool _filterUnpaid;
    [ObservableProperty] private bool _filterHeld;
    [ObservableProperty] private bool _filterPaid;
    [ObservableProperty] private string _todaySummary = "";
    [ObservableProperty] private string _lblToday = "Today";
    [ObservableProperty] private string _lblRefresh = "Refresh";
    [ObservableProperty] private string _lblFilterAll = "All";
    [ObservableProperty] private string _lblFilterUnpaid = "Unpaid";
    [ObservableProperty] private string _lblFilterHeld = "Held";
    [ObservableProperty] private string _lblFilterPaid = "Paid";
    [ObservableProperty] private string _lblDetail = "Order detail";
    [ObservableProperty] private string _lblOpenOnSell = "Open on Sell";
    [ObservableProperty] private string _lblReprintKitchen = "Reprint kitchen";
    [ObservableProperty] private string _lblReprintFront = "Reprint receipt";
    [ObservableProperty] private string _lblVoid = "Void (PIN)";
    [ObservableProperty] private string _lblReopen = "Reopen (PIN)";

    public void RefreshUiLabels()
    {
        LblToday = UiText.Today;
        LblRefresh = UiText.Refresh;
        LblFilterAll = UiText.FilterAll;
        LblFilterUnpaid = UiText.FilterUnpaid;
        LblFilterHeld = UiText.FilterHeld;
        LblFilterPaid = UiText.FilterPaid;
        LblDetail = UiText.OrderDetail;
        LblOpenOnSell = UiText.OpenOnSell;
        LblReprintKitchen = UiText.ReprintKitchen;
        LblReprintFront = UiText.ReprintFront;
        LblVoid = UiText.VoidOrder;
        LblReopen = UiText.ReopenOrder;
        Refresh();
    }

    public void Refresh()
    {
        Orders.Clear();
        foreach (var o in _app.Orders.GetTodayFiltered(Filter))
            Orders.Add(o);
        if (SelectedOrder is not null)
            SelectedOrder = Orders.FirstOrDefault(o => o.Id == SelectedOrder.Id);

        var all = _app.Orders.GetToday();
        var active = all.Where(o => o.Status is not (PosOrderStatus.Voided or PosOrderStatus.Cancelled)).ToList();
        var paidDone = active.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList();
        // Include partial tenders on Sent/Held (cash already in drawer)
        var cash = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.Cash).Sum(t => t.Amount);
        var card = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.CardManual).Sum(t => t.Amount);
        var online = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.OnlinePaid).Sum(t => t.Amount);
        var dueOpen = active.Where(o => o.IsUnpaid).Sum(o => o.BalanceDue);
        var unpaid = active.Count(o => o.IsUnpaid && o.Status != PosOrderStatus.Held);
        var held = active.Count(o => o.Status == PosOrderStatus.Held);
        TodaySummary = UiText.Pick(
            $"Taken: Cash £{cash:0.00} · Card £{card:0.00} · Online £{online:0.00} · Open due £{dueOpen:0.00} · Paid tickets {paidDone.Count} · Unpaid {unpaid} · Held {held}",
            $"已收：现金 £{cash:0.00} · 刷卡 £{card:0.00} · 线上 £{online:0.00} · 未结待收 £{dueOpen:0.00} · 付清单 {paidDone.Count} · 未付 {unpaid} · 挂单 {held}");
    }

    [RelayCommand]
    private void SetFilter(string? filter)
    {
        Filter = filter ?? "All";
        FilterAll = Filter.Equals("All", StringComparison.OrdinalIgnoreCase);
        FilterUnpaid = Filter.Equals("Unpaid", StringComparison.OrdinalIgnoreCase);
        FilterHeld = Filter.Equals("Held", StringComparison.OrdinalIgnoreCase);
        FilterPaid = Filter.Equals("Paid", StringComparison.OrdinalIgnoreCase);
        Refresh();
    }

    partial void OnSelectedOrderChanged(PosOrder? value)
    {
        if (value is null)
        {
            DetailText = "";
            return;
        }

        var lines = string.Join("\n", value.Lines.Select(l =>
            $"{l.Quantity}x {l.Name} £{l.LineTotal:0.00}" +
            (l.KitchenSent ? " [SENT]" : " [NEW]") +
            (string.IsNullOrWhiteSpace(l.Notes) ? "" : $" ({l.Notes})")));
        var tenders = value.Tenders.Count == 0
            ? UiText.Pick("No payments yet", "尚未收款")
            : string.Join("\n", value.Tenders.Select(t =>
                $"  {t.Type} £{t.Amount:0.00}" +
                (t.CashReceived is > 0 ? $" (tendered £{t.CashReceived:0.00})" : "") +
                (t.ChangeGiven is > 0 ? $" change £{t.ChangeGiven:0.00}" : "")));
        DetailText =
            $"{value.OrderNumber}  {value.OrderType}  {value.Source}  {value.Status}\n" +
            (string.IsNullOrWhiteSpace(value.HoldLabel) ? "" : $"Hold: {value.HoldLabel}\n") +
            (string.IsNullOrWhiteSpace(value.TableNumber) ? "" : $"Table: {value.TableNumber}\n") +
            $"{value.CustomerName} {value.CustomerPhone}\n" +
            $"{value.DeliveryAddress} {value.DeliveryPostcode}\n" +
            $"{lines}\n" +
            $"Subtotal £{value.Subtotal:0.00}  Delivery £{value.DeliveryFee:0.00}  Total £{value.Total:0.00}\n" +
            $"Paid £{value.AmountPaid:0.00}  Due £{value.BalanceDue:0.00}\n" +
            $"{tenders}\n" +
            $"Kitchen={(value.KitchenPrinted ? "Y" : "N")} Front={(value.FrontPrinted ? "Y" : "N")}\n" +
            $"Notes: {value.Notes}" +
            (string.IsNullOrWhiteSpace(value.VoidReason) ? "" : $"\nVoid: {value.VoidReason}");
    }

    [RelayCommand]
    private void OpenOnSell()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus(UiText.Pick("Voided — cannot open", "已作废，无法打开"));
            return;
        }
        if (SelectedOrder.Status is PosOrderStatus.Paid or PosOrderStatus.Completed)
        {
            _setStatus(UiText.Pick("Fully paid — use Reopen (PIN) to add items", "已付清 — 用「重开加菜」继续"));
            return;
        }
        _openOnSell?.Invoke(SelectedOrder);
    }

    /// <summary>Industry: reopen a paid ticket (manager PIN) to add items and collect the new balance.</summary>
    [RelayCommand]
    private async Task ReopenOrderAsync()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus(UiText.Pick("Voided — cannot reopen", "已作废，无法重开"));
            return;
        }
        if (SelectedOrder.Status is not (PosOrderStatus.Paid or PosOrderStatus.Completed))
        {
            // Unpaid — just open
            _openOnSell?.Invoke(SelectedOrder);
            return;
        }
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), UiText.Pick("Reopen order", "重开订单")))
            return;
        if (!await UiPrompt.ConfirmAsync(
                UiText.Pick("Reopen paid order?", "重开已付订单？"),
                UiText.Pick(
                    $"Reopen {SelectedOrder.OrderNumber}? Previous payments £{SelectedOrder.AmountPaid:0.00} stay on the ticket. New dishes create a balance due.",
                    $"重开 {SelectedOrder.OrderNumber}？已付 £{SelectedOrder.AmountPaid:0.00} 保留，新加菜产生待收款。")))
            return;

        SelectedOrder.Status = PosOrderStatus.Sent;
        SelectedOrder.UpdatedAt = DateTimeOffset.Now;
        // Keep tenders; if still fully paid until dishes added, IsUnpaid stays false
        _app.Orders.Upsert(SelectedOrder);
        _setStatus(UiText.Pick(
            $"Reopened {SelectedOrder.OrderNumber} — add items, then collect balance",
            $"已重开 {SelectedOrder.OrderNumber} — 可加菜，再收尾款"));
        _openOnSell?.Invoke(SelectedOrder);
        Refresh();
    }

    [RelayCommand]
    private async Task VoidOrderAsync()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus("Already voided");
            return;
        }
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), "Void order"))
            return;
        var paidNote = SelectedOrder.AmountPaid > 0
            ? UiText.Pick(
                $" WARNING: £{SelectedOrder.AmountPaid:0.00} already taken — refund customer manually if needed.",
                $" 注意：已收 £{SelectedOrder.AmountPaid:0.00} — 如需退款请人工处理。")
            : "";
        var reason = await UiPrompt.PromptTextAsync(
            UiText.Pick("Void reason", "作废原因") + paidNote,
            UiText.Pick("Reason", "原因"),
            initial: "Staff error");
        if (reason is null) return;
        try
        {
            var printVoid = _app.GetSettings().PrintVoidKitchenTicket;
            await _app.Print.VoidOrderAsync(SelectedOrder, reason.Trim(), printVoid);
            _setStatus($"Voided {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Void failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReprintKitchenAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.Print.PrintKitchenAsync(SelectedOrder, isReprint: true);
            _setStatus($"Reprinted kitchen {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Reprint kitchen failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReprintFrontAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.Print.PrintFrontAsync(SelectedOrder);
            _setStatus($"Reprinted front {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Reprint front failed: {ex.Message}");
        }
    }
}

public partial class OnlineViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;

    public OnlineViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
        _app.OnlinePoller.OrderReceived += OnOrderReceived;
    }

    public ObservableCollection<PosOrder> Orders { get; } = [];

    [ObservableProperty] private PosOrder? _selectedOrder;
    [ObservableProperty] private string _pollerStatus = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _pollingEnabled;
    [ObservableProperty] private bool _credentialsOk;
    [ObservableProperty] private bool _setupNeeded = true;
    [ObservableProperty] private string _setupHint = "";
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string _lblToggle = "Toggle accepting";
    [ObservableProperty] private string _lblAccepting = "Accepting: OFF";
    [ObservableProperty] private string _lblAdvanced = "Advanced";
    [ObservableProperty] private string _lblPollOnce = "Poll once";
    [ObservableProperty] private string _lblTest = "Test connection";
    [ObservableProperty] private string _lblReprintKitchen = "Reprint kitchen";
    [ObservableProperty] private string _lblAck = "Ack printed";
    [ObservableProperty] private string _lblOrdersTitle = "Online orders";
    [ObservableProperty] private string _lblDetail = "Detail";
    [ObservableProperty] private string _lblSetupTitle = "Setup needed";

    public void RefreshUiLabels()
    {
        LblAdvanced = UiText.Advanced;
        LblPollOnce = UiText.PollOnce;
        LblTest = UiText.TestConnection;
        LblReprintKitchen = UiText.ReprintKitchen;
        LblAck = UiText.AckPrinted;
        LblOrdersTitle = UiText.OnlineOrders;
        LblDetail = UiText.Detail;
        LblSetupTitle = UiText.SetupNeeded;
        Refresh();
    }

    public void Refresh()
    {
        var s = _app.GetSettings();
        PollingEnabled = s.OnlinePollingEnabled && _app.OnlinePoller.IsRunning;
        CredentialsOk = !string.IsNullOrWhiteSpace(s.OnlineUsername) && !string.IsNullOrWhiteSpace(s.OnlinePassword);
        SetupNeeded = !CredentialsOk;
        SetupHint = BuildSetupHint(s, CredentialsOk, PollingEnabled);
        LblToggle = PollingEnabled ? UiText.OnlineToggleOn : UiText.OnlineToggleOff;
        LblAccepting = PollingEnabled ? UiText.AcceptingYes : UiText.AcceptingNo;
        PollerStatus = string.IsNullOrWhiteSpace(_app.OnlinePoller.LastStatus)
            ? (PollingEnabled
                ? UiText.Pick("Accepting online orders…", "正在接线上单…")
                : UiText.Pick("Online OFF", "线上接单：关"))
            : _app.OnlinePoller.LastStatus;
        Orders.Clear();
        foreach (var o in _app.Orders.GetOnlineRecent())
            Orders.Add(o);
    }

    private static string BuildSetupHint(AppSettings s, bool credsOk, bool polling)
    {
        var lines = new List<string>();
        if (!credsOk)
        {
            lines.Add(UiText.Pick(
                "Settings → Online: paste a/u/p from website Admin → Print, then Save.",
                "设置 → 线上：粘贴网站后台 a/u/p，然后保存。"));
            lines.Add(UiText.Pick(
                "EPOS will NOT receive orders until credentials are saved.",
                "未保存凭据前，EPOS 不会收到线上单。"));
        }
        else if (!polling)
            lines.Add(UiText.Pick(
                "Credentials OK. Tap the big switch to accept online orders.",
                "凭据已就绪。点大按钮开始接线上单。"));
        else
            lines.Add(UiText.Pick(
                $"Polling every {s.OnlinePollIntervalSeconds}s. Turn off GcAnyOrder phone while EPOS is on.",
                $"每 {s.OnlinePollIntervalSeconds} 秒拉取。EPOS 开着时请关掉手机 GcAnyOrder。"));
        return string.Join("\n", lines);
    }

    private void OnOrderReceived(object? sender, PosOrder order)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    partial void OnSelectedOrderChanged(PosOrder? value)
    {
        if (value is null) { DetailText = ""; return; }
        DetailText =
            $"{value.OrderNumber}\n{value.CustomerName} {value.CustomerPhone}\n" +
            $"{value.DeliveryAddress}\nTotal £{value.Total:0.00}\n" +
            $"Acked={(value.OnlineAcked ? "Y" : "N")} Kitchen={(value.KitchenPrinted ? "Y" : "N")}\n" +
            string.Join("\n", value.Lines.Select(l => $"{l.Quantity}x {l.Name}"));
    }

    private bool EnsureCredentials(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.OnlineUsername) && !string.IsNullOrWhiteSpace(s.OnlinePassword))
            return true;
        var msg = "Missing Online username/password. Settings → Online (Advanced).";
        PollerStatus = msg;
        SetupHint = BuildSetupHint(s, false, false);
        SetupNeeded = true;
        CredentialsOk = false;
        _setStatus(msg);
        return false;
    }

    [RelayCommand]
    private async Task ToggleAcceptingAsync()
    {
        if (PollingEnabled)
            await StopPollerAsync();
        else
            await StartPollerAsync();
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    private async Task StartPollerAsync()
    {
        var s = _app.GetSettings();
        if (!EnsureCredentials(s)) return;

        s.OnlinePollingEnabled = true;
        _app.SaveSettings(s);
        _app.OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(s));
        await _app.OnlinePoller.StartAsync();
        try { await _app.OnlinePoller.PollOnceAsync(); }
        catch (Exception ex) { PollerStatus = ex.Message; }
        Refresh();
        _setStatus(CredentialsOk ? "Online accepting ON" : PollerStatus);
    }

    [RelayCommand]
    private async Task StopPollerAsync()
    {
        var s = _app.GetSettings();
        s.OnlinePollingEnabled = false;
        _app.SaveSettings(s);
        await _app.OnlinePoller.StopAsync();
        PollingEnabled = false;
        Refresh();
        _setStatus("Online accepting OFF");
    }

    [RelayCommand]
    private async Task PollOnceAsync()
    {
        var s = _app.GetSettings();
        if (!EnsureCredentials(s)) return;
        _app.OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(s));
        try
        {
            await _app.OnlinePoller.PollOnceAsync();
            await Task.Delay(400);
            Refresh();
            _setStatus(_app.OnlinePoller.LastStatus);
        }
        catch (Exception ex)
        {
            PollerStatus = ex.Message;
            _setStatus($"Poll failed: {ex.Message}");
            Refresh();
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var s = _app.GetSettings();
        if (!EnsureCredentials(s)) return;
        _app.OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(s));
        try
        {
            await _app.OnlinePoller.PollOnceAsync();
            await Task.Delay(400);
            Refresh();
            var msg = _app.OnlinePoller.LastStatus.StartsWith("No order", StringComparison.OrdinalIgnoreCase)
                ? "Connection OK — queue empty (or order already claimed)."
                : _app.OnlinePoller.LastStatus;
            PollerStatus = msg;
            _setStatus(msg);
        }
        catch (Exception ex)
        {
            PollerStatus = $"Connection failed: {ex.Message}";
            _setStatus(PollerStatus);
            Refresh();
        }
    }

    [RelayCommand]
    private async Task ReprintKitchenAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.Print.PrintKitchenAsync(SelectedOrder, true);
            _setStatus($"Reprinted online kitchen {SelectedOrder.OrderNumber}");
        }
        catch (Exception ex)
        {
            _setStatus(ex.Message);
        }
    }

    [RelayCommand]
    private async Task AckAgainAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.OnlinePoller.AckPrintedAsync(SelectedOrder.OrderNumber);
            SelectedOrder.OnlineAcked = true;
            _app.Orders.Upsert(SelectedOrder);
            Refresh();
            _setStatus($"Acked {SelectedOrder.OrderNumber}");
        }
        catch (Exception ex)
        {
            _setStatus($"Ack failed: {ex.Message}");
        }
    }
}

public partial class CustomersViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action<Customer>? _startOrder;

    public CustomersViewModel(AppServices app, Action<string> setStatus, Action<Customer>? startOrder = null)
    {
        _app = app;
        _setStatus = setStatus;
        _startOrder = startOrder;
    }

    public ObservableCollection<Customer> Customers { get; } = [];

    [ObservableProperty] private Customer? _selectedCustomer;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editPhone = "";
    [ObservableProperty] private string _editAddress = "";
    [ObservableProperty] private string _editPostcode = "";
    [ObservableProperty] private string _simulatePhone = "01212966775";
    [ObservableProperty] private string _lblTitle = "Phone book";
    [ObservableProperty] private string _wmSearch = "Search name / phone";
    [ObservableProperty] private string _lblCustomer = "Customer";
    [ObservableProperty] private string _wmName = "Name";
    [ObservableProperty] private string _wmPhone = "Phone";
    [ObservableProperty] private string _wmAddress = "Address";
    [ObservableProperty] private string _wmPostcode = "Postcode";
    [ObservableProperty] private string _lblSave = "Save customer";
    [ObservableProperty] private string _lblStartOrder = "Start order";
    [ObservableProperty] private string _lblCallerId = "Caller ID simulate";
    [ObservableProperty] private string _lblCallerHint = "";
    [ObservableProperty] private string _lblSimulate = "Simulate incoming call";

    public void RefreshUiLabels()
    {
        LblTitle = UiText.PhoneBook;
        WmSearch = UiText.SearchCustomer;
        LblCustomer = UiText.Customer;
        WmName = UiText.CustomerName;
        WmPhone = UiText.CustomerPhone;
        WmAddress = UiText.Address;
        WmPostcode = UiText.Postcode;
        LblSave = UiText.SaveCustomer;
        LblStartOrder = UiText.StartOrder;
        LblCallerId = UiText.CallerIdSim;
        LblCallerHint = UiText.CallerIdHint;
        LblSimulate = UiText.SimulateCall;
    }

    public void Refresh()
    {
        Customers.Clear();
        foreach (var c in string.IsNullOrWhiteSpace(SearchText) ? _app.Customers.ListAll() : _app.Customers.Search(SearchText))
            Customers.Add(c);
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCustomerChanged(Customer? value)
    {
        if (value is null) return;
        EditName = value.Name;
        EditPhone = value.Phone;
        var addr = value.Addresses.FirstOrDefault();
        EditAddress = addr?.Line1 ?? "";
        EditPostcode = addr?.Postcode ?? "";
    }

    [RelayCommand]
    private void SaveCustomer()
    {
        if (string.IsNullOrWhiteSpace(EditPhone))
        {
            _setStatus("Phone required");
            return;
        }

        var c = SelectedCustomer ?? _app.Customers.FindByPhone(EditPhone) ?? new Customer();
        c.Name = EditName.Trim();
        c.Phone = EditPhone.Trim();
        if (!string.IsNullOrWhiteSpace(EditAddress) || !string.IsNullOrWhiteSpace(EditPostcode))
        {
            c.Addresses =
            [
                new CustomerAddress
                {
                    Line1 = EditAddress.Trim(),
                    Postcode = EditPostcode.Trim(),
                    IsDefault = true,
                }
            ];
        }
        _app.Customers.Upsert(c);
        Refresh();
        SelectedCustomer = Customers.FirstOrDefault(x => x.Id == c.Id);
        _setStatus($"Saved customer {c.Name}");
    }

    [RelayCommand]
    private void StartOrder()
    {
        if (SelectedCustomer is null)
        {
            _setStatus("Select a customer first");
            return;
        }
        _startOrder?.Invoke(SelectedCustomer);
    }

    [RelayCommand]
    private void SimulateCallerId()
    {
        _app.CallerId.Simulate(SimulatePhone.Trim());
        _setStatus($"Simulated CID {SimulatePhone}");
    }
}
