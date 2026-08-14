using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;
using RingOrder.Epos.Online;

namespace RingOrder.Epos.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    public EposDb Db { get; }
    public SettingsRepository Settings { get; }
    public MenuRepository Menu { get; }
    public OrderRepository Orders { get; }
    public PrintJobRepository PrintJobs { get; }
    public CustomerRepository Customers { get; }
    public StaffRepository Staff { get; }
    public ShiftRepository Shifts { get; }
    public AuditRepository Audit { get; }
    public BundleImporter BundleImporter { get; }
    public PrintDeviceRepository PrintDevices { get; }
    public PrintQueue PrintQueue { get; }
    public BackupService Backups { get; }
    public PosSession Session { get; }
    public PrintService Print { get; }
    public OnlineOrderPoller OnlinePoller { get; }
    public SimulatedCallerId CallerId { get; }
    public ManualCardTerminal CardTerminal { get; }

    private AppSettings _cachedSettings;

    /// <summary>Set when the till has no catalogue yet and needs provisioning.</summary>
    public bool NeedsProvisioning { get; private set; }

    /// <summary>What the last bundle import did, when one happened at startup.</summary>
    public ImportReport? StartupImport { get; private set; }

    private AppServices()
    {
        Db = new EposDb();
        Db.Migrate(AppLog.For("db"));
        Settings = new SettingsRepository(Db);
        Menu = new MenuRepository(Db);
        Orders = new OrderRepository(Db);
        PrintJobs = new PrintJobRepository(Db);
        Customers = new CustomerRepository(Db);
        Staff = new StaffRepository(Db);
        Shifts = new ShiftRepository(Db);
        Audit = new AuditRepository(Db);
        PrintDevices = new PrintDeviceRepository(Db);
        BundleImporter = new BundleImporter(Menu, Settings, Staff, PrintDevices);
        Session = new PosSession(Staff, Shifts, Audit);
        ProvisionIfNeeded();

        _cachedSettings = Settings.Load();

        CardTerminal = new ManualCardTerminal();
        CallerId = new SimulatedCallerId();
        OnlinePoller = new OnlineOrderPoller();
        OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(_cachedSettings));
        Print = new PrintService(this);
        PrintQueue = new PrintQueue(PrintJobs, PrintDevices, AppLog.For("print"));
        PrintQueue.Start();
        Backups = new BackupService(Db);
        Backups.Start();

        // The queue is work, not an archive: printed jobs older than a week are
        // dead weight, and their payloads are raster bitmaps.
        PrintJobs.PurgePrintedBefore(DateTimeOffset.Now.AddDays(-7));
    }

    public static AppServices Start()
    {
        Instance = new AppServices();
        return Instance;
    }

    /// <summary>
    /// First run on a merchant's PC: the installer drops the shop bundle into
    /// the profile folder and the till builds itself from it. An empty catalogue
    /// with no bundle is not an error — it is a till waiting to be set up, and
    /// saying so beats seeding someone else's menu.
    /// </summary>
    private void ProvisionIfNeeded()
    {
        if (Menu.CountItems() > 0) return;

        var bundle = Directory
            .EnumerateFiles(LocalPaths.ProfileDirectory, "*.ringpos.json")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (bundle is null)
        {
            NeedsProvisioning = true;
            return;
        }

        try
        {
            StartupImport = BundleImporter.ImportFromFile(bundle);
            AppLog.Info("provision", StartupImport.Summary);
            foreach (var warning in StartupImport.Warnings)
                AppLog.Warn("provision", warning);
        }
        catch (Exception ex)
        {
            NeedsProvisioning = true;
            AppLog.Error("provision", $"{Path.GetFileName(bundle)} failed to import", ex);
        }
    }

    public AppSettings GetSettings() => _cachedSettings;

    public void SaveSettings(AppSettings settings)
    {
        Settings.Save(settings);
        _cachedSettings = settings;
        OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(settings));
    }

    public AppSettings ReloadSettings()
    {
        _cachedSettings = Settings.Load();
        return _cachedSettings;
    }
}
