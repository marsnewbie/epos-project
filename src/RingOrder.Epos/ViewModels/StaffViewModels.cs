using CommunityToolkit.Mvvm.ComponentModel;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.ViewModels;

/// <summary>One row of the staff list in Settings.</summary>
public partial class StaffRow : ObservableObject
{
    public StaffRow(StaffMember member, bool isCurrent)
    {
        Member = member;
        IsCurrent = isCurrent;
        _selectedRole = member.Role;
    }

    public StaffMember Member { get; }
    public bool IsCurrent { get; }

    public string Name => Member.Name;
    public bool IsActive => Member.IsActive;

    /// <summary>Shown beside the name: the state someone needs to act on.</summary>
    public string Status => !Member.IsActive
        ? "Off"
        : Member.MustChangePin
            ? "PIN not changed"
            : IsCurrent ? "Signed in" : "";

    public bool HasStatus => Status.Length > 0;
    public bool NeedsAttention => Member.IsActive && Member.MustChangePin;
    public string ActiveLabel => Member.IsActive ? "Switch off" : "Switch on";

    [ObservableProperty] private StaffRole _selectedRole;
}

/// <summary>One printer in Settings, editable in place.</summary>
public partial class PrinterRow : ObservableObject
{
    public PrinterRow(PrintDevice device)
    {
        Device = device;
        _name = device.Name;
        _transport = device.Transport;
        _address = device.Address;
        _paperWidth = device.PaperWidthMm;
        _hasCashDrawer = device.HasCashDrawer;
        _isEnabled = device.IsEnabled;
    }

    public PrintDevice Device { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private PrintTransport _transport;
    [ObservableProperty] private string _address;
    [ObservableProperty] private int _paperWidth;
    [ObservableProperty] private bool _hasCashDrawer;
    [ObservableProperty] private bool _isEnabled;

    /// <summary>Last reachability check, so setup is not guesswork.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsGood;

    /// <summary>What to type in the address box, which differs per transport.</summary>
    public string AddressHint => Transport switch
    {
        PrintTransport.Tcp => "192.168.1.50  or  192.168.1.50:9100",
        PrintTransport.Serial => "COM3  or  COM3:9600",
        PrintTransport.File => "folder for ticket files (blank = temp)",
        _ => "Windows printer queue name",
    };

    partial void OnTransportChanged(PrintTransport value) => OnPropertyChanged(nameof(AddressHint));

    public PrintDevice ToDomain()
    {
        Device.Name = Name.Trim();
        Device.Transport = Transport;
        Device.Address = Address.Trim();
        Device.PaperWidthMm = PaperWidth >= 80 ? 80 : 58;
        Device.HasCashDrawer = HasCashDrawer;
        Device.IsEnabled = IsEnabled;
        return Device;
    }
}

/// <summary>One routing rule in Settings.</summary>
public partial class RouteRow : ObservableObject
{
    public RouteRow(PrintRoute route, IReadOnlyDictionary<string, PrintDevice> devices)
    {
        Route = route;
        _summary = route.Describe(devices);
        _isEnabled = route.IsEnabled;
    }

    public PrintRoute Route { get; }

    [ObservableProperty] private string _summary;
    [ObservableProperty] private bool _isEnabled;
}
