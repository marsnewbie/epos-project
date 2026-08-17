namespace RingOrder.Epos.Domain;

/// <summary>
/// How the till reaches a printer.
/// <para>
/// A shop's printers are rarely all the same. The counter machine is usually
/// USB with a driver, the kitchen is usually on the network, and Bluetooth
/// turns up on cheap hardware a merchant already owns.
/// </para>
/// </summary>
public enum PrintTransport
{
    /// <summary>A Windows print queue. Covers USB, and anything with a driver.</summary>
    WindowsQueue,

    /// <summary>
    /// Raw TCP to port 9100. Preferred for kitchen printers: it skips the
    /// Windows spooler, whose stuck jobs are a routine source of support calls,
    /// and it can be asked whether the printer has paper.
    /// </summary>
    Tcp,

    /// <summary>A serial port, including the virtual one a Bluetooth pairing presents.</summary>
    Serial,

    /// <summary>Writes the ticket to a file. For development and for support.</summary>
    File,
}

/// <summary>What a ticket is for. Decides the template, not the destination.</summary>
public enum PrintDocument
{
    Kitchen,
    Receipt,
    Report,
}

public sealed class PrintDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PrintTransport Transport { get; set; } = PrintTransport.WindowsQueue;

    /// <summary>Queue name, <c>host</c> or <c>host:port</c>, or COM port.</summary>
    public string Address { get; set; } = "";

    public int PaperWidthMm { get; set; } = 80;
    public string Encoding { get; set; } = "gbk";

    /// <summary>
    /// Render CJK as a bitmap. Cheap printers carry unreliable Chinese fonts,
    /// and a kitchen ticket of question marks is worse than a slow one.
    /// </summary>
    public bool CjkAsRaster { get; set; } = true;

    public bool HasCashDrawer { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Dots across the paper, which is what the renderer lays out to.</summary>
    public int PaperDots => PaperWidthMm >= 80 ? 576 : 384;

    /// <summary>Characters per line in Font A at that width.</summary>
    public int Columns => PaperWidthMm >= 80 ? 48 : 32;
}

/// <summary>
/// One routing rule: what to print, where, how many times.
/// <para>
/// Rules are matched in order and every match fires — a dish can print at the
/// wok and again on the packing bench, and that is a shop asking for two
/// copies in two places, not a conflict.
/// </para>
/// </summary>
public sealed class PrintRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;

    public PrintDocument Document { get; set; } = PrintDocument.Kitchen;

    /// <summary>Station to match, or null for every station.</summary>
    public string? PrintClass { get; set; }

    /// <summary>Service type to match, or null for all.</summary>
    public ServiceType? ServiceType { get; set; }

    /// <summary>Channel to match, or null for all.</summary>
    public OrderChannel? Channel { get; set; }

    public string DeviceId { get; set; } = "";
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Where the ticket goes when the target cannot be reached. A kitchen
    /// printer that is off should mean the front printer produces the ticket
    /// with a banner on it, not that the order is silently lost.
    /// </summary>
    public string? FallbackDeviceId { get; set; }

    public bool Matches(PrintDocument document, string? printClass, ServiceType serviceType, OrderChannel channel) =>
        IsEnabled &&
        Document == document &&
        (PrintClass is null || string.Equals(PrintClass, printClass, StringComparison.OrdinalIgnoreCase)) &&
        (ServiceType is null || ServiceType == serviceType) &&
        (Channel is null || Channel == channel);

    public string Describe(IReadOnlyDictionary<string, PrintDevice> devices)
    {
        var what = Document == PrintDocument.Kitchen
            ? PrintClass ?? "all stations"
            : Document.ToString().ToLowerInvariant();
        var when = string.Join(", ", new[]
        {
            ServiceType?.ToString(),
            Channel?.ToString(),
        }.Where(x => x is not null));

        var target = devices.TryGetValue(DeviceId, out var device) ? device.Name : "(missing printer)";
        var copies = Copies > 1 ? $" x{Copies}" : "";
        return when.Length == 0 ? $"{what} → {target}{copies}" : $"{what} ({when}) → {target}{copies}";
    }
}

/// <summary>A ticket waiting to come out of a specific printer.</summary>
public sealed class PrintJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderId { get; set; } = "";
    public string OrderNumber { get; set; } = "";

    public string DeviceId { get; set; } = "";
    public PrintDocument Document { get; set; } = PrintDocument.Kitchen;

    /// <summary>Which rendering was asked for: full ticket, additions only, void.</summary>
    public string Template { get; set; } = "kitchen";

    public int Copies { get; set; } = 1;
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;

    /// <summary>
    /// The rendered bytes, stored with the job. A ticket must print what it
    /// said when it was queued, even if the menu or the order changed while it
    /// sat waiting for paper.
    /// </summary>
    public byte[] Payload { get; set; } = [];

    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>Retry backoff: the worker leaves it alone until this time.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? PrintedAt { get; set; }

    /// <summary>Given up on, and shown to staff so they can reprint deliberately.</summary>
    public bool IsAbandoned => Status == PrintJobStatus.Failed && Attempts >= MaxAttempts;

    public const int MaxAttempts = 5;
}

/// <summary>
/// Resolves what should print where. Pure — no I/O — so the rules can be
/// tested without a printer, which is the only way they will be tested often.
/// </summary>
public static class PrintRouting
{
    public sealed record Target(PrintDevice Device, int Copies, string? FallbackDeviceId);

    /// <summary>
    /// Devices that should receive the kitchen ticket for an order, with the
    /// lines each of them is responsible for.
    /// </summary>
    public static List<(Target Target, List<CartLine> Lines)> RouteKitchen(
        PosOrder order,
        IReadOnlyList<CartLine> lines,
        IReadOnlyList<PrintRoute> routes,
        IReadOnlyDictionary<string, PrintDevice> devices)
    {
        var byDevice = new Dictionary<string, (Target Target, List<CartLine> Lines)>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            foreach (var route in routes.Where(r =>
                         r.Matches(PrintDocument.Kitchen, line.PrintClass, order.ServiceType, order.Channel))
                     .OrderBy(r => r.SortOrder))
            {
                if (!devices.TryGetValue(route.DeviceId, out var device) || !device.IsEnabled)
                    continue;

                if (!byDevice.TryGetValue(device.Id, out var entry))
                {
                    entry = (new Target(device, route.Copies, route.FallbackDeviceId), []);
                    byDevice[device.Id] = entry;
                }

                if (!entry.Lines.Contains(line))
                    entry.Lines.Add(line);
            }
        }

        return byDevice.Values.Where(e => e.Lines.Count > 0).ToList();
    }

    /// <summary>Devices that should receive a whole-order document.</summary>
    public static List<Target> Route(
        PosOrder order,
        PrintDocument document,
        IReadOnlyList<PrintRoute> routes,
        IReadOnlyDictionary<string, PrintDevice> devices)
    {
        var targets = new List<Target>();
        foreach (var route in routes
                     .Where(r => r.Matches(document, null, order.ServiceType, order.Channel))
                     .OrderBy(r => r.SortOrder))
        {
            if (!devices.TryGetValue(route.DeviceId, out var device) || !device.IsEnabled)
                continue;
            if (targets.Any(t => t.Device.Id == device.Id))
                continue;
            targets.Add(new Target(device, route.Copies, route.FallbackDeviceId));
        }
        return targets;
    }

    /// <summary>
    /// Devices for a document that belongs to no order — the shift reading.
    /// <para>
    /// Service type and channel are not consulted, because a report has neither.
    /// A rule that narrows on them was written about tickets, and applying it
    /// here would silently swallow the one document an owner prints by hand.
    /// </para>
    /// </summary>
    public static List<Target> RouteStandalone(
        PrintDocument document,
        IReadOnlyList<PrintRoute> routes,
        IReadOnlyDictionary<string, PrintDevice> devices)
    {
        var targets = new List<Target>();
        foreach (var route in routes
                     .Where(r => r.IsEnabled && r.Document == document)
                     .OrderBy(r => r.SortOrder))
        {
            if (!devices.TryGetValue(route.DeviceId, out var device) || !device.IsEnabled)
                continue;
            if (targets.Any(t => t.Device.Id == device.Id))
                continue;
            targets.Add(new Target(device, route.Copies, route.FallbackDeviceId));
        }
        return targets;
    }

    /// <summary>
    /// A shop with no rules at all still has to print. This is what a fresh
    /// till uses until someone configures it: everything to the first device.
    /// </summary>
    public static List<PrintRoute> DefaultRoutes(IReadOnlyList<PrintDevice> devices)
    {
        if (devices.Count == 0) return [];

        var front = devices.FirstOrDefault(d => d.HasCashDrawer) ?? devices[0];
        var kitchen = devices.FirstOrDefault(d => d.Id != front.Id) ?? front;

        return
        [
            new PrintRoute
            {
                SortOrder = 0,
                Document = PrintDocument.Kitchen,
                DeviceId = kitchen.Id,
                FallbackDeviceId = kitchen.Id == front.Id ? null : front.Id,
            },
            new PrintRoute
            {
                SortOrder = 1,
                Document = PrintDocument.Receipt,
                DeviceId = front.Id,
            },
        ];
    }
}
