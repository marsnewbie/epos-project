using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

/// <summary>One delivery on the board, with a tick box for sending or settling.</summary>
public partial class DispatchRow : ObservableObject
{
    public DispatchRow(DispatchEntry entry, string driverName)
    {
        Entry = entry;
        Order = entry.Order;

        Where = string.IsNullOrWhiteSpace(Order.DeliveryPostcode)
            ? TicketAddress()
            : $"{TicketAddress()} · {Order.DeliveryPostcode}";

        Money = entry.CarriesCash ? $"£{entry.CashToCollect:0.00} to collect" : "Paid";
        CarriesCash = entry.CarriesCash;
        DriverName = driverName;

        // Stated on the row rather than enforced. The person holding the bag can
        // see things the till cannot.
        Concern = DispatchBoard.ConcernAboutSending(Order);
        HasConcern = Concern is not null;

        Waited = entry.Stage == DeliveryStage.WithDriver && Order.DispatchedAt is { } sent
            ? $"out {(int)(DateTimeOffset.Now - sent).TotalMinutes} min"
            : $"{(int)(DateTimeOffset.Now - Order.CreatedAt).TotalMinutes} min ago";
    }

    public DispatchEntry Entry { get; }
    public PosOrder Order { get; }

    public string Number => Order.OrderNumber;
    public string Customer => string.IsNullOrWhiteSpace(Order.CustomerName) ? "—" : Order.CustomerName!;
    public string Where { get; }
    public string Money { get; }
    public bool CarriesCash { get; }
    public string DriverName { get; }
    public string Waited { get; }
    public string? Concern { get; }
    public bool HasConcern { get; }

    [ObservableProperty] private bool _isPicked;

    private string TicketAddress() =>
        string.IsNullOrWhiteSpace(Order.DeliveryAddress) ? "no address" : Order.DeliveryAddress!;
}

/// <summary>
/// The dispatch board: what is waiting, what is on the road, and how much of
/// the shop's money each driver is carrying.
/// <para>
/// Only reachable when the shop grades someone as a driver. A merchant whose
/// deliveries all go through Uber Eats never sees it.
/// </para>
/// </summary>
public partial class DispatchViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;

    public DispatchViewModel(AppServices app, Action<string> setStatus)
    {
        _app = app;
        _setStatus = setStatus;
    }

    public ObservableCollection<DispatchRow> Waiting { get; } = [];
    public ObservableCollection<DispatchRow> OnTheRoad { get; } = [];
    public ObservableCollection<DriverLoad> Loads { get; } = [];
    public ObservableCollection<StaffMember> Drivers { get; } = [];

    [ObservableProperty] private StaffMember? _selectedDriver;
    [ObservableProperty] private string _cashOutSummary = "";
    [ObservableProperty] private bool _hasCashOut;
    [ObservableProperty] private bool _isEmpty = true;

    public void Refresh()
    {
        var orders = _app.Dispatch.TodaysDeliveries();
        var board = DispatchBoard.Build(orders);

        Drivers.Clear();
        foreach (var driver in _app.Dispatch.AvailableDrivers()) Drivers.Add(driver);
        SelectedDriver ??= Drivers.FirstOrDefault();

        Waiting.Clear();
        OnTheRoad.Clear();
        foreach (var entry in board)
        {
            var row = new DispatchRow(entry, NameOf(entry.Order.DriverStaffId));
            if (entry.Stage == DeliveryStage.Waiting) Waiting.Add(row);
            else if (entry.Stage == DeliveryStage.WithDriver) OnTheRoad.Add(row);
        }

        Loads.Clear();
        foreach (var load in DispatchBoard.Loads(orders, NameOf)) Loads.Add(load);

        var outstanding = DispatchBoard.CashOutWithDrivers(orders);
        HasCashOut = outstanding > 0;
        CashOutSummary = $"£{outstanding:0.00} still out with drivers";
        IsEmpty = Waiting.Count == 0 && OnTheRoad.Count == 0;
    }

    private string NameOf(string? staffId) =>
        string.IsNullOrWhiteSpace(staffId) ? "—" : _app.Staff.GetById(staffId)?.Name ?? "(removed)";

    [RelayCommand]
    private void SendOut()
    {
        var picked = Waiting.Where(r => r.IsPicked).Select(r => r.Order).ToList();
        if (picked.Count == 0) { _setStatus("Tick the deliveries to send"); return; }
        if (SelectedDriver is not { } driver) { _setStatus("No driver to send them with"); return; }

        _app.Dispatch.SendOut(picked, driver);
        _setStatus($"{picked.Count} delivery(s) out with {driver.Name}");
        Refresh();
    }

    /// <summary>
    /// A driver is back. Whatever they were carrying goes into the drawer and
    /// the deliveries close.
    /// </summary>
    [RelayCommand]
    private void SettleReturn()
    {
        var picked = OnTheRoad.Where(r => r.IsPicked).Select(r => r.Order).ToList();
        if (picked.Count == 0) { _setStatus("Tick the deliveries that came back"); return; }

        var collected = _app.Dispatch.Settle(picked);
        _setStatus(collected > 0
            ? $"{picked.Count} delivered · £{collected:0.00} cash in"
            : $"{picked.Count} delivered · nothing owed");
        Refresh();
    }

    /// <summary>Everything one driver is holding, in one action at the end of a run.</summary>
    [RelayCommand]
    private void SettleWholeRun(DriverLoad? load)
    {
        if (load is null) return;

        var theirs = OnTheRoad
            .Where(r => r.Order.DriverStaffId == load.StaffId)
            .Select(r => r.Order)
            .ToList();
        if (theirs.Count == 0) return;

        var collected = _app.Dispatch.Settle(theirs);
        _setStatus($"{load.Name} back · {theirs.Count} delivered · £{collected:0.00} cash in");
        Refresh();
    }

    /// <summary>Back on the counter. Money already taken is not touched.</summary>
    [RelayCommand]
    private void Recall()
    {
        var picked = OnTheRoad.Where(r => r.IsPicked).Select(r => r.Order).ToList();
        if (picked.Count == 0) { _setStatus("Tick the deliveries to bring back"); return; }

        foreach (var order in picked) _app.Dispatch.Recall(order);
        _setStatus($"{picked.Count} delivery(s) back on the counter");
        Refresh();
    }
}
