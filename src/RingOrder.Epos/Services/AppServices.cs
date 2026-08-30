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
    public AddressRepository Addresses { get; }
    public AddressCacheRepository AddressCache { get; }
    public AddressLookupService AddressLookup { get; }
    public DataRetention Retention { get; }
    public DeliveryZoneRepository DeliveryZones { get; }
    public MilesBandRepository MilesBands { get; }
    public RoadDistanceService RoadDistance { get; } = new();
    public RefundRepository RefundRepo { get; }
    public RefundService Refunds { get; }
    public ShiftReportService ShiftReports { get; }
    public DispatchService Dispatch { get; }
    public PrintQueue PrintQueue { get; }
    public BackupService Backups { get; }
    public PosSession Session { get; }
    public EntitlementStore EntitlementStore { get; }

    /// <summary>
    /// What this till is licensed to do. Resolved from disk at startup and
    /// refreshed in the background — never on the path to opening.
    /// </summary>
    public EntitlementService Entitlement { get; }
    public PrintService Print { get; }
    public OnlineOrderPoller OnlinePoller { get; }
    /// <summary>
    /// The simulator, always constructed. Settings → Hardware raises a test
    /// call through it whatever the real provider is, so the popup can be
    /// demonstrated on a till with no box attached.
    /// </summary>
    public SimulatedCallerId CallerId { get; }

    /// <summary>The provider actually listening — the simulator, or the serial box.</summary>
    public ICallerIdProvider CallerIdProvider { get; }

    public IPaymentTerminal CardTerminal { get; }

    private AppSettings _cachedSettings;

    /// <summary>Set when the till has no catalogue yet and needs provisioning.</summary>
    public bool NeedsProvisioning { get; private set; }

    /// <summary>What a restore did at this startup, when one was pending.</summary>
    public string? RestoreOutcome { get; private set; }

    /// <summary>What the last bundle import did, when one happened at startup.</summary>
    public ImportReport? StartupImport { get; private set; }

    private AppServices()
    {
        // Before anything opens the database. A restore requested from Settings
        // is carried out here, on a file nothing is holding — see RestoreRequest.
        if (RestoreRequest.ApplyPending(m => AppLog.Info("restore", m)) is { } outcome)
            RestoreOutcome = outcome;

        Db = new EposDb();
        Db.Migrate(AppLog.For("db"));
        Settings = new SettingsRepository(Db);
        Menu = new MenuRepository(Db);
        Orders = new OrderRepository(Db);
        PrintJobs = new PrintJobRepository(Db);
        Addresses = new AddressRepository(Db);
        Customers = new CustomerRepository(Db, Addresses);
        Retention = new DataRetention(Db);
        DeliveryZones = new DeliveryZoneRepository(Db);
        MilesBands = new MilesBandRepository(Db);
        RefundRepo = new RefundRepository(Db);
        MoveAddressesOutOfCustomerRows();
        Staff = new StaffRepository(Db);
        Shifts = new ShiftRepository(Db);
        Audit = new AuditRepository(Db);
        PrintDevices = new PrintDeviceRepository(Db);
        AddressCache = new AddressCacheRepository(Db);
        BundleImporter = new BundleImporter(Menu, Settings, Staff, PrintDevices, DeliveryZones);
        Session = new PosSession(Staff, Shifts, Audit);
        ProvisionIfNeeded();

        // Secrets written before DPAPI existed are encrypted here, once. Said
        // out loud in the log because the old value has already travelled: it is
        // in every backup taken until now, and in any database sent to support.
        if (Settings.ProtectStoredSecrets())
            AppLog.Warn("settings",
                "encrypted secrets that were stored in the clear. Earlier backups still " +
                "contain the readable values — rotate the website password and lookup key.");

        _cachedSettings = Settings.Load();

        // Built from the settings on every call rather than captured once, so
        // pasting an API key in Settings takes effect on the next lookup instead
        // of on the next restart.
        AddressLookup = new AddressLookupService(
            AddressCache,
            Addresses,
            () => AddressLookupFactory.Create(
                _cachedSettings.AddressLookupProvider, _cachedSettings.AddressLookupApiKey),
            () => _cachedSettings.AddressLookupCacheEnabled);

        CardTerminal = _cachedSettings.CardTerminalMode switch
        {
            "simulated" => new SimulatedPaymentTerminal(),
            _ => new ManualCardTerminal(),
        };

        CallerId = new SimulatedCallerId();
        CallerIdProvider = _cachedSettings is { CallerIdEnabled: true, CallerIdMode: "serial" }
            ? new SerialModemCallerId(
                _cachedSettings.CallerIdComPort,
                _cachedSettings.CallerIdBaud,
                message => AppLog.Warn("callerid", message))
            : CallerId;
        OnlinePoller = new OnlineOrderPoller();
        OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(_cachedSettings));
        Print = new PrintService(this);
        Refunds = new RefundService(this);
        ShiftReports = new ShiftReportService(this);
        Dispatch = new DispatchService(this);
        PrintQueue = new PrintQueue(PrintJobs, PrintDevices, AppLog.For("print"));
        PrintQueue.Start();

        // Listening costs nothing until the phone rings, and a provider that
        // cannot open its port retries in the background rather than failing
        // startup — a till must open whether or not the caller box is plugged in.
        if (!ReferenceEquals(CallerIdProvider, CallerId))
        {
            _ = CallerIdProvider.StartAsync().ContinueWith(
                t => AppLog.Error("callerid", "could not start", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        Backups = new BackupService(Db);
        Backups.Start();

        // Resolved from what is already stored, so this costs a couple of
        // queries and no network. The ask happens afterwards and its failure is
        // invisible: a shop whose router is off must not be able to tell.
        EntitlementStore = new EntitlementStore(Db);
        Entitlement = new EntitlementService(EntitlementStore, () => _cachedSettings);
        Entitlement.RefreshInBackground();

        // The queue is work, not an archive: printed jobs older than a week are
        // dead weight, and their payloads are raster bitmaps.
        PrintJobs.PurgePrintedBefore(DateTimeOffset.Now.AddDays(-7));

        SweepDormantCustomersIfAsked();
    }

    public static AppServices Start()
    {
        Instance = new AppServices();
        return Instance;
    }

    /// <summary>
    /// Removes dormant customer records, but only where the shop has both set a
    /// period and asked for it to happen on its own.
    /// <para>
    /// Two switches rather than one because this deletes a merchant's phone book.
    /// Retention is their decision as data controller, and an automatic default
    /// would be us making it for them.
    /// </para>
    /// </summary>
    private void SweepDormantCustomersIfAsked()
    {
        var settings = _cachedSettings;
        if (!settings.CustomerRetentionAutomatic || settings.CustomerRetentionMonths <= 0) return;

        try
        {
            var dormant = Retention.FindDormant(settings.CustomerRetentionMonths, DateTimeOffset.Now);
            if (dormant.Count == 0) return;

            var outcome = Retention.Erase(dormant.Select(d => d.Id).ToList());

            // Counts only — an audit trail that repeated the names would reinstate
            // exactly what was just erased.
            AppLog.Info("privacy",
                $"retention sweep at {settings.CustomerRetentionMonths} months: {outcome.Summary}");
            Audit.Record(new AuditEntry
            {
                Action = "customers.erased.retention",
                Detail = outcome.Summary,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("privacy", "retention sweep failed", ex);
        }
    }

    /// <summary>
    /// Completes migration 5 in code. A no-op on every start after the first, and
    /// resumable if the first one was interrupted — see <see cref="AddressBackfill"/>
    /// for why the fingerprint cannot be computed in SQL.
    /// </summary>
    private void MoveAddressesOutOfCustomerRows()
    {
        try
        {
            var report = AddressBackfill.Run(Db, Addresses);
            if (!report.DidWork) return;

            AppLog.Info("addresses", report.Summary);
            foreach (var warning in report.Warnings)
                AppLog.Warn("addresses", warning);
        }
        catch (Exception ex)
        {
            // The old blob is only emptied once its rows are written, so a
            // failure here leaves the data where it was and the till still opens.
            AppLog.Error("addresses", "moving addresses out of customer rows failed", ex);
        }
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
