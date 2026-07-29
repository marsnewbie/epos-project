using MagicWok.Epos.Data;
using MagicWok.Epos.Domain;
using MagicWok.Epos.Hardware;
using MagicWok.Epos.Online;

namespace MagicWok.Epos.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    public EposDb Db { get; }
    public SettingsRepository Settings { get; }
    public MenuRepository Menu { get; }
    public OrderRepository Orders { get; }
    public PrintJobRepository PrintJobs { get; }
    public CustomerRepository Customers { get; }
    public MenuSeeder MenuSeeder { get; }
    public PrintService Print { get; }
    public OnlineOrderPoller OnlinePoller { get; }
    public SimulatedCallerId CallerId { get; }
    public WindowsEscPosPrinter KitchenPrinter { get; }
    public WindowsEscPosPrinter FrontPrinter { get; }
    public ICashDrawer CashDrawer { get; }
    public ManualCardTerminal CardTerminal { get; }

    private AppSettings _cachedSettings;

    private AppServices()
    {
        Db = new EposDb();
        Db.EnsureCreated();
        Settings = new SettingsRepository(Db);
        Menu = new MenuRepository(Db);
        Orders = new OrderRepository(Db);
        PrintJobs = new PrintJobRepository(Db);
        Customers = new CustomerRepository(Db);
        MenuSeeder = new MenuSeeder(Menu, Settings);
        MenuSeeder.EnsureSeeded();

        _cachedSettings = Settings.Load();
        // Persist migrated online endpoints (Goodcom URL → EPOS JSON) once.
        Settings.Save(_cachedSettings);

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
                if (order.Status is PosOrderStatus.Draft or PosOrderStatus.Open or PosOrderStatus.Held)
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
