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

    public void Refresh()
    {
        Orders.Clear();
        foreach (var o in _app.Orders.GetTodayFiltered(Filter))
            Orders.Add(o);
        if (SelectedOrder is not null)
            SelectedOrder = Orders.FirstOrDefault(o => o.Id == SelectedOrder.Id);

        var all = _app.Orders.GetToday();
        var paid = all.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList();
        var cash = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.Cash).Sum(t => t.Amount);
        var card = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.CardManual).Sum(t => t.Amount);
        var online = paid.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.OnlinePaid).Sum(t => t.Amount);
        TodaySummary =
            $"Today: {paid.Count} paid · Cash £{cash:0.00} · Card £{card:0.00} · Online £{online:0.00} · " +
            $"Unpaid {all.Count(o => o.IsUnpaid)} · Held {all.Count(o => o.Status == PosOrderStatus.Held)}";
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
        DetailText =
            $"{value.OrderNumber}  {value.OrderType}  {value.Source}  {value.Status}\n" +
            (string.IsNullOrWhiteSpace(value.HoldLabel) ? "" : $"Hold: {value.HoldLabel}\n") +
            (string.IsNullOrWhiteSpace(value.TableNumber) ? "" : $"Table: {value.TableNumber}\n") +
            $"{value.CustomerName} {value.CustomerPhone}\n" +
            $"{value.DeliveryAddress} {value.DeliveryPostcode}\n" +
            $"{lines}\n" +
            $"Subtotal £{value.Subtotal:0.00}  Delivery £{value.DeliveryFee:0.00}  Total £{value.Total:0.00}\n" +
            $"Kitchen={(value.KitchenPrinted ? "Y" : "N")} Front={(value.FrontPrinted ? "Y" : "N")}\n" +
            $"Notes: {value.Notes}" +
            (string.IsNullOrWhiteSpace(value.VoidReason) ? "" : $"\nVoid: {value.VoidReason}");
    }

    [RelayCommand]
    private void OpenOnSell()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Paid or PosOrderStatus.Completed or PosOrderStatus.Voided)
        {
            _setStatus("Paid/voided — use Reprint only");
            return;
        }
        _openOnSell?.Invoke(SelectedOrder);
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
        var reason = await UiPrompt.PromptTextAsync("Void reason", "Reason / 原因", initial: "Staff error");
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

    public void Refresh()
    {
        var s = _app.GetSettings();
        PollingEnabled = s.OnlinePollingEnabled && _app.OnlinePoller.IsRunning;
        CredentialsOk = !string.IsNullOrWhiteSpace(s.OnlineUsername) && !string.IsNullOrWhiteSpace(s.OnlinePassword);
        SetupNeeded = !CredentialsOk;
        SetupHint = BuildSetupHint(s, CredentialsOk, PollingEnabled);
        PollerStatus = string.IsNullOrWhiteSpace(_app.OnlinePoller.LastStatus)
            ? (PollingEnabled ? "Accepting online orders…" : "Online OFF")
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
            lines.Add("Settings → Online (Advanced): paste a/u/p from website Admin → Print, then Save.");
            lines.Add("EPOS will NOT receive orders until credentials are saved.");
        }
        else if (!polling)
            lines.Add("Credentials OK. Tap the big switch to accept online orders.");
        else
            lines.Add($"Polling every {s.OnlinePollIntervalSeconds}s. Turn off GcAnyOrder phone while EPOS is on.");
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
