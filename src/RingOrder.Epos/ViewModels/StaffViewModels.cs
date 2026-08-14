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

/// <summary>
/// One routing rule in Settings, editable in place.
/// <para>
/// Every dropdown offers "any" as its first entry, because most rules are broad
/// — "kitchen dishes go to the kitchen printer" — and only a few need narrowing
/// to one service type or channel.
/// </para>
/// </summary>
public partial class RouteRow : ObservableObject
{
    /// <summary>Shown as the first option wherever a filter is optional.</summary>
    public const string Any = "(any)";

    public RouteRow(PrintRoute route, IReadOnlyList<PrintDevice> devices)
    {
        Route = route;
        Devices = devices;
        DeviceChoices = devices.Select(d => d.Name).ToArray();
        FallbackChoices = ["(none)", .. DeviceChoices];

        _isEnabled = route.IsEnabled;
        _document = route.Document;
        _printClass = route.PrintClass ?? Any;
        _serviceType = route.ServiceType?.ToString() ?? Any;
        _channel = route.Channel?.ToString() ?? Any;
        _deviceName = devices.FirstOrDefault(d => d.Id == route.DeviceId)?.Name ?? "";
        _copies = Math.Clamp(route.Copies, 1, 9);
        _fallbackName = devices.FirstOrDefault(d => d.Id == route.FallbackDeviceId)?.Name ?? "(none)";
    }

    public PrintRoute Route { get; }
    public IReadOnlyList<PrintDevice> Devices { get; }

    public string[] DeviceChoices { get; }
    public string[] FallbackChoices { get; }

    public PrintDocument[] Documents { get; } = Enum.GetValues<PrintDocument>();
    public string[] PrintClassChoices { get; } = [Any, .. Domain.PrintClass.Known];
    public string[] ServiceTypeChoices { get; } = [Any, .. Enum.GetNames<ServiceType>()];
    public string[] ChannelChoices { get; } = [Any, .. Enum.GetNames<OrderChannel>()];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private PrintDocument _document;
    [ObservableProperty] private string _printClass;
    [ObservableProperty] private string _serviceType;
    [ObservableProperty] private string _channel;
    [ObservableProperty] private string _deviceName;
    [ObservableProperty] private int _copies;
    [ObservableProperty] private string _fallbackName;

    /// <summary>A station only means something on a kitchen ticket.</summary>
    public bool ShowsPrintClass => Document == PrintDocument.Kitchen;

    partial void OnDocumentChanged(PrintDocument value) => OnPropertyChanged(nameof(ShowsPrintClass));

    public PrintRoute ToDomain()
    {
        Route.IsEnabled = IsEnabled;
        Route.Document = Document;
        Route.PrintClass = Document == PrintDocument.Kitchen && PrintClass != Any ? PrintClass : null;
        Route.ServiceType = ServiceType != Any && Enum.TryParse<ServiceType>(ServiceType, out var st) ? st : null;
        Route.Channel = Channel != Any && Enum.TryParse<OrderChannel>(Channel, out var ch) ? ch : null;
        Route.DeviceId = Devices.FirstOrDefault(d => d.Name == DeviceName)?.Id ?? Route.DeviceId;
        Route.Copies = Math.Clamp(Copies, 1, 9);
        Route.FallbackDeviceId = Devices.FirstOrDefault(d => d.Name == FallbackName)?.Id;
        return Route;
    }
}

/// <summary>One VAT band in Settings.</summary>
public partial class TaxClassRow : ObservableObject
{
    public TaxClassRow(TaxClass taxClass)
    {
        Class = taxClass;
        _name = taxClass.Name;
        _ratePercent = taxClass.RateBasisPoints / 100m;
    }

    public TaxClass Class { get; }
    public string Id => Class.Id;

    [ObservableProperty] private string _name;

    /// <summary>
    /// Entered as a percentage because that is how a rate is quoted, and stored
    /// as basis points so 20% is 2000 and never 0.19999999.
    /// </summary>
    [ObservableProperty] private decimal _ratePercent;

    public TaxClass ToDomain()
    {
        Class.Name = Name.Trim();
        Class.RateBasisPoints = (int)decimal.Round(RatePercent * 100m, 0, MidpointRounding.AwayFromZero);
        return Class;
    }
}
