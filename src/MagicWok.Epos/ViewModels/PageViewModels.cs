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

    public OrdersViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
    }

    public ObservableCollection<PosOrder> Orders { get; } = [];

    [ObservableProperty] private PosOrder? _selectedOrder;
    [ObservableProperty] private string _detailText = "";

    public void Refresh()
    {
        Orders.Clear();
        foreach (var o in _app.Orders.GetToday())
            Orders.Add(o);
        if (SelectedOrder is not null)
            SelectedOrder = Orders.FirstOrDefault(o => o.Id == SelectedOrder.Id);
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
            (string.IsNullOrWhiteSpace(l.Notes) ? "" : $" ({l.Notes})")));
        DetailText =
            $"{value.OrderNumber}  {value.OrderType}  {value.Source}  {value.Status}\n" +
            $"{value.CustomerName} {value.CustomerPhone}\n" +
            $"{value.DeliveryAddress} {value.DeliveryPostcode}\n" +
            $"{lines}\n" +
            $"Subtotal £{value.Subtotal:0.00}  Delivery £{value.DeliveryFee:0.00}  Total £{value.Total:0.00}\n" +
            $"Kitchen={(value.KitchenPrinted ? "Y" : "N")} Front={(value.FrontPrinted ? "Y" : "N")}\n" +
            $"Notes: {value.Notes}";
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

    public void Refresh()
    {
        var s = _app.GetSettings();
        PollingEnabled = s.OnlinePollingEnabled && _app.OnlinePoller.IsRunning;
        CredentialsOk = !string.IsNullOrWhiteSpace(s.OnlineUsername) && !string.IsNullOrWhiteSpace(s.OnlinePassword);
        SetupNeeded = !CredentialsOk || !PollingEnabled;
        SetupHint = BuildSetupHint(s, CredentialsOk, PollingEnabled);
        PollerStatus = string.IsNullOrWhiteSpace(_app.OnlinePoller.LastStatus)
            ? (PollingEnabled ? "Polling…" : "Idle")
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
            lines.Add("1) Open website Admin → Print");
            lines.Add("2) Copy Res ID (a), Username (u), Password (p) into EPOS Settings → Online");
            lines.Add("3) Save Settings, then come back here and tap Start poller");
            lines.Add("EPOS will NOT receive orders until a/u/p are saved.");
        }
        else if (!polling)
        {
            lines.Add("Credentials OK. Tap Start poller (or Poll once) to fetch website orders.");
            lines.Add("Turn off GcAnyOrder phone app while EPOS is polling — only one device can claim each order.");
        }
        else
        {
            lines.Add($"Polling every {s.OnlinePollIntervalSeconds}s → {s.OnlineOrderServerUrl}");
            lines.Add("If an order was already claimed by the phone app, wait ~2 min or reset print status in Admin.");
        }
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
        var msg = "Missing Online username/password. Settings → Online: paste a/u/p from website Admin → Print, then Save.";
        PollerStatus = msg;
        SetupHint = BuildSetupHint(s, false, false);
        SetupNeeded = true;
        CredentialsOk = false;
        _setStatus(msg);
        return false;
    }

    [RelayCommand]
    private async Task StartPollerAsync()
    {
        var s = _app.GetSettings();
        if (!EnsureCredentials(s)) return;

        s.OnlinePollingEnabled = true;
        _app.SaveSettings(s);
        _app.OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(s));
        await _app.OnlinePoller.StartAsync();
        // Kick immediately so user sees result without waiting interval
        try { await _app.OnlinePoller.PollOnceAsync(); }
        catch (Exception ex) { PollerStatus = ex.Message; }
        Refresh();
        _setStatus(CredentialsOk ? "Online poller started" : PollerStatus);
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
        _setStatus("Online poller stopped");
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
            // Give MainViewModel handler a moment to upsert/print
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
                ? "Connection OK — queue empty (or order already claimed by another device)."
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

    public CustomersViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
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
    private void SimulateCallerId()
    {
        _app.CallerId.Simulate(SimulatePhone.Trim());
        _setStatus($"Simulated CID {SimulatePhone}");
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;

    public SettingsViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
        Reload();
    }

    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _shopAddress = "";
    [ObservableProperty] private string _shopPostcode = "";
    [ObservableProperty] private string _shopPhone = "";
    [ObservableProperty] private string _kitchenPrinter = "GlPrinter80";
    [ObservableProperty] private string _frontPrinter = "GlPrinter80";
    [ObservableProperty] private string _printEncoding = "gbk";
    [ObservableProperty] private bool _printChineseAsRaster = true;
    [ObservableProperty] private bool _openDrawerOnCash = true;
    [ObservableProperty] private bool _sendKitchenOnSend = true;
    [ObservableProperty] private bool _printFrontOnPay = true;
    [ObservableProperty] private bool _autoKitchenPrintOnline = true;
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
        SendKitchenOnSend = s.SendKitchenOnSend;
        PrintFrontOnPay = s.PrintFrontOnPay;
        AutoKitchenPrintOnline = s.AutoKitchenPrintOnline;
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
        MenuInfo = $"Items: {_app.Menu.CountItems()} | Last import: {s.LastMenuImportAt ?? "n/a"}";
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
        s.SendKitchenOnSend = SendKitchenOnSend;
        s.PrintFrontOnPay = PrintFrontOnPay;
        s.AutoKitchenPrintOnline = AutoKitchenPrintOnline;
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
        _app.SaveSettings(s);
        _setStatus("Settings saved");
        MenuInfo = $"Items: {_app.Menu.CountItems()} | Last import: {s.LastMenuImportAt ?? "n/a"}";
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
