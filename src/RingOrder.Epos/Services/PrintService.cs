using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;

namespace RingOrder.Epos.Services;

/// <summary>
/// Turns an order into tickets on the shop's printers.
/// <para>
/// Queueing is synchronous and cannot fail for want of paper; delivery happens
/// on <see cref="PrintQueue"/> behind the counter. That is the whole design:
/// the sale is never waiting on hardware.
/// </para>
/// <para>
/// A ticket is marked sent when it is <em>queued</em>, not when paper appears.
/// The alternative loses either way — waiting for paper before marking means
/// two people can send the same lines twice while the first job is retrying,
/// and the queue is durable, so a queued ticket does arrive or is shown as
/// abandoned. "Paper is the truth" governs the job's own status, which is what
/// the reprint list and the printer light read.
/// </para>
/// </summary>
public sealed class PrintService
{
    private readonly AppServices _app;

    public PrintService(AppServices app) => _app = app;

    /// <summary>
    /// Kitchen tickets. Each station's printer gets the lines it is responsible
    /// for; with no rules configured, everything goes to one device.
    /// </summary>
    public Task<int> PrintKitchenAsync(
        PosOrder order, bool isReprint = false, bool unsentOnly = false, bool isVoid = false)
    {
        var settings = _app.GetSettings();
        var devices = _app.PrintDevices.GetDeviceMap();
        var routes = _app.PrintDevices.GetRoutes();

        var lines = unsentOnly
            ? order.Lines.Where(l => !l.KitchenSent).ToList()
            : order.Lines.ToList();
        if (lines.Count == 0) lines = order.Lines.ToList();

        var targets = PrintRouting.RouteKitchen(order, lines, routes, devices);
        if (targets.Count == 0)
        {
            // No rule matched. Rather than drop a kitchen ticket — the one
            // output a kitchen cannot work around — fall back to any printer.
            var any = devices.Values.FirstOrDefault(d => d.IsEnabled);
            if (any is not null)
                targets = [(new PrintRouting.Target(any, 1, null), lines)];
        }

        var queued = 0;
        foreach (var (target, deviceLines) in targets)
        {
            var payload = TicketRenderer.RenderKitchen(
                order, settings,
                unsentOnly: unsentOnly && !isReprint && !isVoid,
                isVoid: isVoid,
                device: target.Device,
                onlyLines: deviceLines);

            Enqueue(order, target, PrintDocument.Kitchen,
                isVoid ? "kitchen-void" : unsentOnly ? "kitchen-additions" : "kitchen", payload);
            queued++;
        }

        if (!isVoid)
        {
            var now = DateTimeOffset.Now;
            foreach (var line in lines)
            {
                line.KitchenSent = true;
                line.KitchenSentAt ??= now;
            }

            order.KitchenPrinted = true;
            // Never change business status on a reprint, and never un-hold a
            // ticket by printing it.
            if (!isReprint && order.Status is PosOrderStatus.Draft or PosOrderStatus.Open)
                order.Status = PosOrderStatus.Sent;
        }

        order.UpdatedAt = DateTimeOffset.Now;
        _app.Orders.Upsert(order);
        _app.PrintQueue.Wake();
        return Task.FromResult(queued);
    }

    /// <summary>
    /// The refund slip, routed like a receipt because it goes to the customer
    /// standing at the same counter.
    /// <para>
    /// The order is not re-saved here. A refund does not change the sale, and
    /// writing the order back would only risk overwriting it with whatever the
    /// caller happened to be holding.
    /// </para>
    /// </summary>
    public Task<int> PrintRefundAsync(PosOrder order, Refund refund)
    {
        var settings = _app.GetSettings();
        var devices = _app.PrintDevices.GetDeviceMap();
        var targets = PrintRouting.Route(order, PrintDocument.Receipt, _app.PrintDevices.GetRoutes(), devices);

        if (targets.Count == 0)
        {
            var any = devices.Values.FirstOrDefault(d => d.IsEnabled);
            if (any is null) return Task.FromResult(0);
            targets = [new PrintRouting.Target(any, 1, null)];
        }

        foreach (var target in targets)
        {
            var payload = TicketRenderer.RenderRefund(
                order, refund, settings, target.Device, _app.Menu.GetTaxClasses());
            Enqueue(order, target, PrintDocument.Receipt, "refund", payload);
        }

        return Task.FromResult(targets.Count);
    }

    public Task<int> PrintReceiptAsync(PosOrder order)
    {
        var settings = _app.GetSettings();
        var devices = _app.PrintDevices.GetDeviceMap();
        var targets = PrintRouting.Route(order, PrintDocument.Receipt, _app.PrintDevices.GetRoutes(), devices);

        if (targets.Count == 0)
        {
            var any = devices.Values.FirstOrDefault(d => d.IsEnabled);
            if (any is null) return Task.FromResult(0);
            targets = [new PrintRouting.Target(any, 1, null)];
        }

        foreach (var target in targets)
        {
            var payload = TicketRenderer.RenderFront(
                order, settings, target.Device, _app.Menu.GetTaxClasses());
            Enqueue(order, target, PrintDocument.Receipt, "receipt", payload);
        }

        order.FrontPrinted = true;
        order.UpdatedAt = DateTimeOffset.Now;
        _app.Orders.Upsert(order);
        _app.PrintQueue.Wake();
        return Task.FromResult(targets.Count);
    }

    /// <summary>
    /// The X or Z reading, on paper.
    /// <para>
    /// Routed to a Report rule if the shop has one, otherwise to the printer the
    /// drawer hangs off — which is the counter machine, which is where whoever
    /// is counting the drawer is standing.
    /// </para>
    /// </summary>
    public Task<int> PrintShiftReportAsync(ShiftReport report)
    {
        var settings = _app.GetSettings();
        var devices = _app.PrintDevices.GetDeviceMap();
        var targets = PrintRouting.RouteStandalone(
            PrintDocument.Report, _app.PrintDevices.GetRoutes(), devices);

        if (targets.Count == 0)
        {
            var enabled = devices.Values.Where(d => d.IsEnabled).ToList();
            var front = enabled.FirstOrDefault(d => d.HasCashDrawer) ?? enabled.FirstOrDefault();
            if (front is null) return Task.FromResult(0);
            targets = [new PrintRouting.Target(front, 1, null)];
        }

        foreach (var target in targets)
        {
            var payload = TicketRenderer.RenderShiftReport(report, settings, target.Device);

            // No order behind this one, so the job carries the shift number as
            // its reference. The reprint list shows that rather than a blank.
            _app.PrintJobs.Enqueue(new PrintJob
            {
                OrderId = "",
                OrderNumber = $"{report.Title} {report.ShiftNumber}",
                DeviceId = target.Device.Id,
                Document = PrintDocument.Report,
                Template = report.Kind == ShiftReportKind.Z ? "z-report" : "x-report",
                Copies = target.Copies,
                Status = PrintJobStatus.Pending,
                Payload = payload,
            });
        }

        _app.PrintQueue.Wake();
        return Task.FromResult(targets.Count);
    }

    public async Task VoidOrderAsync(PosOrder order, string reason, bool printKitchen)
    {
        order.Status = PosOrderStatus.Voided;
        order.VoidReason = reason;
        order.UpdatedAt = DateTimeOffset.Now;

        // Tenders stay for audit: money that was taken is still money that was
        // taken, and the refund is a separate, deliberate act.
        if (order.AmountPaid > 0)
            order.Notes = string.IsNullOrWhiteSpace(order.Notes)
                ? $"VOID after paid £{order.AmountPaid:0.00} — refund manually if needed"
                : $"{order.Notes}\nVOID after paid £{order.AmountPaid:0.00} — refund manually if needed";

        _app.Orders.Upsert(order);
        _app.Session.Record("order.void", order.Id, $"{order.OrderNumber} — {reason}");

        if (printKitchen && order.KitchenPrinted)
            await PrintKitchenAsync(order, isVoid: true);
    }

    /// <summary>A test page on one device, naming the device it came out of.</summary>
    public async Task TestDeviceAsync(PrintDevice device)
    {
        var payload = TicketRenderer.RenderTestPage(device.Name, _app.GetSettings(), device);
        await PrintTransports.For(device.Transport).SendAsync(device, payload);
    }

    /// <summary>Opens the drawer on whichever printer it is wired to.</summary>
    public async Task OpenDrawerAsync()
    {
        var device = _app.PrintDevices.GetDevices(enabledOnly: true).FirstOrDefault(d => d.HasCashDrawer)
                     ?? _app.PrintDevices.GetDevices(enabledOnly: true).FirstOrDefault()
                     ?? throw new InvalidOperationException("No printer is configured for the cash drawer.");

        await PrintTransports.For(device.Transport).SendAsync(device, EscPos.OpenDrawer);
    }

    public async Task HandleOnlineOrderAsync(PosOrder incoming)
    {
        var existing = !string.IsNullOrWhiteSpace(incoming.OnlineExternalId)
            ? _app.Orders.GetByOnlineExternalId(incoming.OnlineExternalId!)
            : null;

        if (existing is not null)
        {
            incoming = existing;   // idempotent: we already have this one
        }
        else
        {
            if (string.IsNullOrWhiteSpace(incoming.OrderNumber))
                incoming.OrderNumber = _app.Settings.AllocateOrderNumber();
            _app.Session.Stamp(incoming);
            _app.Orders.Upsert(incoming);
        }

        if (_app.GetSettings().AutoKitchenPrintOnline && !incoming.KitchenPrinted)
            await PrintKitchenAsync(incoming);

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
                // Do not loop-reprint. The kitchen has its ticket; a failed
                // acknowledgement can be retried, and a website that sends the
                // same order every four seconds is a far worse failure.
                AppLog.Warn("online", $"ack failed for {incoming.OrderNumber}: {ex.Message}");
            }
        }
    }

    private void Enqueue(
        PosOrder order, PrintRouting.Target target, PrintDocument document, string template, byte[] payload)
    {
        _app.PrintJobs.Enqueue(new PrintJob
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            DeviceId = target.Device.Id,
            Document = document,
            Template = template,
            Copies = target.Copies,
            Status = PrintJobStatus.Pending,
            Payload = payload,
        });
    }
}
