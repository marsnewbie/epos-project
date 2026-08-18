using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

/// <summary>
/// The whole interface of the print-only edition: what arrived, whether it came
/// out, and what to do when it did not.
/// <para>
/// This machine sits in a corner with nobody watching it. Everything here is
/// therefore either a state someone needs to notice from across a room, or a
/// button for the one thing that goes wrong — a ticket that did not print.
/// </para>
/// </summary>
public partial class MonitorViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _refresh;

    public MonitorViewModel(AppServices app)
    {
        _app = app;

        app.OnlinePoller.OrderReceived += async (_, order) =>
        {
            try
            {
                await app.Print.HandleOnlineOrderAsync(order);
            }
            catch (Exception ex)
            {
                AppLog.Error("online", $"handling {order.OrderNumber} failed", ex);
            }
            Dispatcher.UIThread.Post(Refresh);
        };

        // A shop that never touches this machine still needs the screen to be
        // right whenever somebody does look at it.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        ShopName = app.GetSettings().ShopName;
        Refresh();
        _ = StartPollingAsync();
    }

    public ObservableCollection<PosOrder> Recent { get; } = [];
    public ObservableCollection<PrintJob> Failed { get; } = [];

    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _feedLabel = "";
    [ObservableProperty] private bool _feedOn;
    [ObservableProperty] private string _printerLabel = "";
    [ObservableProperty] private bool _printersHealthy = true;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private string _status = "";

    public void Refresh()
    {
        Recent.Clear();
        foreach (var order in _app.Orders.GetRecentByChannel(OrderChannel.Web, take: 20))
            Recent.Add(order);

        Failed.Clear();
        foreach (var job in _app.PrintJobs.GetAbandoned()) Failed.Add(job);
        HasFailed = Failed.Count > 0;

        var devices = _app.PrintDevices.GetDevices(enabledOnly: true);
        var faults = _app.PrintQueue.Faults;
        var working = devices.Count(d => !faults.ContainsKey(d.Id));
        PrintersHealthy = devices.Count > 0 && working == devices.Count && !HasFailed;
        PrinterLabel = devices.Count == 0
            ? "No printer configured"
            : $"{working} of {devices.Count} printers ready";

        var waiting = _app.PrintJobs.CountWaiting();
        if (waiting > 0) PrinterLabel += $" · {waiting} waiting";

        FeedOn = _app.OnlinePoller.IsRunning;
        FeedLabel = FeedOn ? "Receiving web orders" : "Not receiving";
    }

    private async Task StartPollingAsync()
    {
        // The one job this machine has. It starts itself rather than waiting to
        // be switched on, because there is nobody here to switch it on.
        try
        {
            await _app.OnlinePoller.StartAsync();
            Status = "Connected";
        }
        catch (Exception ex)
        {
            Status = $"Cannot reach the website: {ex.Message}";
            AppLog.Error("online", "poller failed to start", ex);
        }
        Refresh();
    }

    /// <summary>
    /// Reprints a ticket the printer gave up on. Deliberate and never automatic:
    /// a queue that retries forever is how a kitchen ends up with forty copies
    /// of one order.
    /// </summary>
    [RelayCommand]
    private async Task ReprintAsync(PrintJob? job)
    {
        if (job is null) return;
        try
        {
            _app.PrintJobs.Requeue(job);
            _app.PrintQueue.Wake();
            Status = $"Reprinting {job.OrderNumber}";
        }
        catch (Exception ex)
        {
            Status = $"Reprint failed: {ex.Message}";
        }
        await Task.CompletedTask;
        Refresh();
    }

    [RelayCommand]
    private async Task ToggleFeedAsync()
    {
        try
        {
            if (_app.OnlinePoller.IsRunning) await _app.OnlinePoller.StopAsync();
            else await _app.OnlinePoller.StartAsync();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        Refresh();
    }
}
