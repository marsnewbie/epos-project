using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

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
        foreach (var o in _app.Orders.GetRecentByChannel(OrderChannel.Web))
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
