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
    public PrintService Print { get; }
    public OnlineOrderPoller OnlinePoller { get; }
    public SimulatedCallerId CallerId { get; }
    public WindowsEscPosPrinter KitchenPrinter { get; }
    public WindowsEscPosPrinter FrontPrinter { get; }
    public ICashDrawer CashDrawer { get; }
    public ManualCardTerminal CardTerminal { get; }

    private AppSettings _cachedSettings;

    /// <summary>Set when the till has no catalogue yet and needs provisioning.</summary>
    public bool NeedsProvisioning { get; private set; }

    /// <summary>What the last bundle import did, when one happened at startup.</summary>
    public ImportReport? StartupImport { get; private set; }

    private AppServices()
    {
        Db = new EposDb();
        Db.Migrate(message => Console.WriteLine($"[db] {message}"));
        Settings = new SettingsRepository(Db);
        Menu = new MenuRepository(Db);
        Orders = new OrderRepository(Db);
        PrintJobs = new PrintJobRepository(Db);
        Customers = new CustomerRepository(Db);
        Staff = new StaffRepository(Db);
        Shifts = new ShiftRepository(Db);
        Audit = new AuditRepository(Db);
        BundleImporter = new BundleImporter(Menu, Settings, Staff);
        ProvisionIfNeeded();

        _cachedSettings = Settings.Load();

        KitchenPrinter = new WindowsEscPosPrinter(_cachedSettings.KitchenPrinterName, () => _cachedSettings);
        FrontPrinter = new WindowsEscPosPrinter(_cachedSettings.FrontPrinterName, () => _cachedSettings);
        CashDrawer = new EscPosCashDrawer(() => _cachedSettings.FrontPrinterName);
        CardTerminal = new ManualCardTerminal();
        CallerId = new SimulatedCallerId();
        OnlinePoller = new OnlineOrderPoller();
        OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(_cachedSettings));
        Print = new PrintService(this);
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
            Console.WriteLine($"[provision] {StartupImport.Summary}");
            foreach (var warning in StartupImport.Warnings)
                Console.WriteLine($"[provision] warning: {warning}");
        }
        catch (Exception ex)
        {
            NeedsProvisioning = true;
            Console.WriteLine($"[provision] {Path.GetFileName(bundle)} failed to import: {ex.Message}");
        }
    }

    public AppSettings GetSettings() => _cachedSettings;

    public void SaveSettings(AppSettings settings)
    {
        Settings.Save(settings);
        _cachedSettings = settings;
        KitchenPrinter.SetName(settings.KitchenPrinterName);
        FrontPrinter.SetName(settings.FrontPrinterName);
        OnlinePoller.Configure(OnlineOrderPollerOptions.FromSettings(settings));
    }

    public AppSettings ReloadSettings()
    {
        _cachedSettings = Settings.Load();
        return _cachedSettings;
    }
}

public sealed class PrintService
{
    private readonly AppServices _app;

    public PrintService(AppServices app) => _app = app;

    public async Task<PrintJob> PrintKitchenAsync(PosOrder order, bool isReprint = false, bool unsentOnly = false, bool isVoid = false)
    {
        var settings = _app.GetSettings();
        var job = new PrintJob
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            Channel = PrintJobChannel.Kitchen,
            Status = PrintJobStatus.Claimed,
            PayloadText = isVoid ? $"void:{order.OrderNumber}" : unsentOnly ? $"kitchen-new:{order.OrderNumber}" : $"kitchen:{order.OrderNumber}",
        };
        _app.PrintJobs.Insert(job);

        try
        {
            var printUnsent = unsentOnly && !isReprint && !isVoid;
            var bytes = TicketRenderer.RenderKitchen(order, settings, unsentOnly: printUnsent, isVoid: isVoid);
            await _app.KitchenPrinter.PrintRawAsync(bytes);
            job.Status = PrintJobStatus.Printed;
            job.PrintedAt = DateTimeOffset.Now;
            job.Attempts++;
            _app.PrintJobs.Update(job);

            if (!isVoid)
            {
                var now = DateTimeOffset.Now;
                foreach (var line in order.Lines)
                {
                    if (printUnsent && line.KitchenSent) continue;
                    line.KitchenSent = true;
                    line.KitchenSentAt ??= now;
                }
                order.KitchenPrinted = true;
                // Never change business status on reprint; never un-hold via print
                if (!isReprint && order.Status is PosOrderStatus.Draft or PosOrderStatus.Open)
                    order.Status = PosOrderStatus.Sent;
            }

            order.UpdatedAt = DateTimeOffset.Now;
            _app.Orders.Upsert(order);
            return job;
        }
        catch (Exception ex)
        {
            job.Status = PrintJobStatus.Failed;
            job.Error = ex.Message;
            job.Attempts++;
            _app.PrintJobs.Update(job);
            throw;
        }
    }

    public async Task VoidOrderAsync(PosOrder order, string reason, bool printKitchen)
    {
        order.Status = PosOrderStatus.Voided;
        order.VoidReason = reason;
        order.UpdatedAt = DateTimeOffset.Now;
        // Keep tenders for audit; AmountPaid remains visible on voided order detail
        if (order.AmountPaid > 0)
            order.Notes = string.IsNullOrWhiteSpace(order.Notes)
                ? $"VOID after paid £{order.AmountPaid:0.00} — refund manually if needed"
                : $"{order.Notes}\nVOID after paid £{order.AmountPaid:0.00} — refund manually if needed";
        _app.Orders.Upsert(order);
        if (printKitchen && order.KitchenPrinted)
            await PrintKitchenAsync(order, isVoid: true);
    }

    public async Task<PrintJob> PrintFrontAsync(PosOrder order)
    {
        var settings = _app.GetSettings();
        var job = new PrintJob
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            Channel = PrintJobChannel.Front,
            Status = PrintJobStatus.Claimed,
            PayloadText = $"front:{order.OrderNumber}",
        };
        _app.PrintJobs.Insert(job);

        try
        {
            var bytes = TicketRenderer.RenderFront(order, settings);
            await _app.FrontPrinter.PrintRawAsync(bytes);
            job.Status = PrintJobStatus.Printed;
            job.PrintedAt = DateTimeOffset.Now;
            job.Attempts++;
            _app.PrintJobs.Update(job);

            order.FrontPrinted = true;
            order.UpdatedAt = DateTimeOffset.Now;
            _app.Orders.Upsert(order);
            return job;
        }
        catch (Exception ex)
        {
            job.Status = PrintJobStatus.Failed;
            job.Error = ex.Message;
            job.Attempts++;
            _app.PrintJobs.Update(job);
            throw;
        }
    }

    public async Task HandleOnlineOrderAsync(PosOrder incoming)
    {
        var existing = !string.IsNullOrWhiteSpace(incoming.OnlineExternalId)
            ? _app.Orders.GetByOnlineExternalId(incoming.OnlineExternalId!)
            : null;

        if (existing is not null)
        {
            // Idempotent: already have this online order
            incoming = existing;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(incoming.OrderNumber))
                incoming.OrderNumber = _app.Settings.AllocateOrderNumber();
            _app.Orders.Upsert(incoming);
        }

        var settings = _app.GetSettings();
        if (settings.AutoKitchenPrintOnline && !incoming.KitchenPrinted)
        {
            await PrintKitchenAsync(incoming);
        }

        if (!incoming.OnlineAcked)
        {
            try
            {
                await _app.OnlinePoller.AckPrintedAsync(incoming.OrderNumber);
                incoming.OnlineAcked = true;
                incoming.UpdatedAt = DateTimeOffset.Now;
                _app.Orders.Upsert(incoming);
            }
            catch (Exception ex)
            {
                // Do not loop-reprint: kitchen already done; ack can retry later.
                Console.WriteLine($"[Online] Ack failed for {incoming.OrderNumber}: {ex.Message}");
            }
        }
    }
}
