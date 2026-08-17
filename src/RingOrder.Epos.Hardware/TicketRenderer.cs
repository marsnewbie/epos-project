using System.Drawing;
using System.Text;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Hardware;

public static class TicketRenderer
{
    private static EscPosTicketBuilder NewBuilder(string encoding, bool raster, PrintDevice? device) =>
        device is null
            ? new EscPosTicketBuilder(encoding, raster)
            : new EscPosTicketBuilder(device.Encoding, device.CjkAsRaster, device.Columns);

    /// <summary>
    /// The kitchen ticket. <paramref name="device"/> decides paper width,
    /// encoding and whether Chinese is rasterised, because a shop's printers
    /// are rarely identical — an 80mm counter machine and a 58mm handheld take
    /// the same ticket laid out differently.
    /// </summary>
    public static byte[] RenderKitchen(PosOrder order, AppSettings settings, bool unsentOnly = false,
        bool isVoid = false, PrintDevice? device = null, IReadOnlyList<CartLine>? onlyLines = null)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var raster = settings.PrintChineseAsRaster;
        var b = NewBuilder(enc, raster, device);
        var shop = string.IsNullOrWhiteSpace(settings.ShopName) ? "KITCHEN" : settings.ShopName;
        // onlyLines is what this device is responsible for: the wok printer
        // gets the wok's dishes, not the whole order.
        var source = onlyLines?.ToList() ?? order.Lines.ToList();
        var lines = unsentOnly ? source.Where(l => !l.KitchenSent).ToList() : source;
        if (lines.Count == 0) lines = source;

        b.Center().Normal().Bold(true).Line(shop.ToUpperInvariant()).Bold(false).Left();
        if (isVoid)
            b.KitchenLine("*** VOID ***", large: true);
        else if (unsentOnly && order.Lines.Any(l => l.KitchenSent))
            b.KitchenLine("*** ADDITIONS ***", large: true);
        b.KitchenLine(TicketHeadline(order), large: true);
        if (ChannelBanner(order) is { } banner)
            b.KitchenLine(banner, large: true);
        b.Normal().Line($"Order No:{order.OrderNumber}");
        b.Line(order.CreatedAt.ToLocalTime().ToString("HH:mm dd-MM-yy"));

        if (!string.IsNullOrWhiteSpace(order.TableNumber))
            b.KitchenLine($"TABLE {order.TableNumber}", large: true);

        var when = FirstNonEmpty(order.RequestedFor, order.FulfilmentLabel);
        if (!string.IsNullOrWhiteSpace(when))
        {
            b.Normal().Line("Requested for:");
            b.KitchenLine(when!, large: true);
        }

        b.Separator('-');

        foreach (var line in lines)
        {
            b.Normal().ItemEnglishAndPrice($"{line.Quantity} x {line.Name}", EscPos.Money(line.LineTotal));

            if (!string.IsNullOrWhiteSpace(line.ItemTranslation))
                b.KitchenLine($"{line.Quantity} {line.ItemTranslation}", large: true);

            foreach (var sel in line.Selections)
            {
                foreach (var c in sel.Choices)
                {
                    b.Normal().Line($"  + {c.Label}");
                    if (!string.IsNullOrWhiteSpace(c.OptionTranslation))
                        b.KitchenLine($"  {c.OptionTranslation}", large: false);
                }
            }

            if (!string.IsNullOrWhiteSpace(line.Notes))
                b.Normal().Line($"  NOTE: {line.Notes}");

            b.Nl();
        }

        b.Separator('-');
        if (!unsentOnly)
        {
            if (order.DiscountTotal > 0)
                b.Normal().ColumnsAscii("Discount", "-" + EscPos.Money(order.DiscountTotal));
            if (order.DeliveryFee > 0)
                b.Normal().ColumnsAscii("Delivery", EscPos.Money(order.DeliveryFee));

            b.Large().Bold(true).ColumnsAscii("Total", EscPos.Money(order.Total), cols: 24).Bold(false).Normal();
            if (order.AmountPaid > 0)
            {
                b.Normal().ColumnsAscii("Paid", EscPos.Money(order.AmountPaid));
                if (!order.IsFullyPaid)
                    b.Normal().ColumnsAscii("DUE", EscPos.Money(order.BalanceDue));
            }
            b.Separator('-');
        }

        var pay = ResolvePayment(order);
        if (!string.IsNullOrWhiteSpace(pay))
            b.KitchenLine($"Payment:{pay}", large: true);

        b.Normal().Line("Cus Info:");
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
            b.Line(order.CustomerName!);
        if (!string.IsNullOrWhiteSpace(order.CustomerPhone))
            b.KitchenLine(order.CustomerPhone!, large: true);

        if (order.ServiceType == ServiceType.Delivery)
        {
            var addr = NormalizeAddress(order.DeliveryAddress);
            foreach (var addrLine in WrapLines(addr, EscPos.ColsFontA))
                b.Normal().Line(addrLine);
            if (!string.IsNullOrWhiteSpace(order.DeliveryPostcode))
                b.Line(order.DeliveryPostcode!);
        }
        else
        {
            b.KitchenLine(TicketHeadline(order), large: true);
        }

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            b.Separator('-');
            b.Bold(true).Line("Comments:").Bold(false);
            foreach (var n in WrapLines(order.Notes, EscPos.ColsFontA))
                b.Line(n);
        }

        if (!string.IsNullOrWhiteSpace(order.TicketFooter))
        {
            b.Separator('-');
            b.Center();
            foreach (var f in WrapLines(order.TicketFooter, EscPos.ColsFontA))
                b.Normal().Line(f!);
            b.Left();
        }

        b.FeedAndCut();
        return b.Build();
    }

    /// <summary>
    /// The customer's receipt. <paramref name="taxClasses"/> is what the shop
    /// has declared; VAT is only printed when the shop has a VAT number, since
    /// most small takeaways are below the registration threshold and must not
    /// show tax they cannot charge.
    /// </summary>
    public static byte[] RenderFront(
        PosOrder order,
        AppSettings settings,
        PrintDevice? device = null,
        IReadOnlyList<TaxClass>? taxClasses = null)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = NewBuilder(enc, settings.PrintChineseAsRaster, device);
        var interim = !order.IsFullyPaid;
        b.Center().KitchenLine(settings.ShopName, large: true);
        b.Normal().Line(settings.ShopAddress);
        b.Line($"{settings.ShopPostcode}  {settings.ShopPhone}");
        if (!string.IsNullOrWhiteSpace(settings.VatNumber))
            b.Line($"VAT No: {settings.VatNumber}");
        b.Line(interim ? "*** INTERIM / NOT PAID IN FULL ***" : "RECEIPT").Left().Separator('=');
        b.Line($"Order {order.OrderNumber}");
        b.Line(order.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        b.Line(TicketHeadline(order));
        b.Separator();
        foreach (var line in order.Lines)
        {
            b.ItemEnglishAndPrice($"{line.Quantity} x {line.Name}", EscPos.Money(line.LineTotal));
            foreach (var sel in line.Selections)
                foreach (var c in sel.Choices)
                    b.Line($"  + {c.Label}");
        }
        b.Separator();
        b.ColumnsAscii("Subtotal", EscPos.Money(order.Subtotal));
        if (order.DeliveryFee > 0) b.ColumnsAscii("Delivery", EscPos.Money(order.DeliveryFee));
        if (order.DiscountTotal > 0) b.ColumnsAscii("Discount", "-" + EscPos.Money(order.DiscountTotal));
        b.Large().Bold(true).ColumnsAscii("TOTAL", EscPos.Money(order.Total), cols: 24).Bold(false).Normal();

        WriteVatSummary(b, order, settings, taxClasses);

        b.Separator('-');
        if (order.Tenders.Count == 0)
        {
            b.ColumnsAscii("Paid", EscPos.Money(0));
            b.Large().Bold(true).ColumnsAscii("BALANCE DUE", EscPos.Money(order.BalanceDue), cols: 24).Bold(false).Normal();
        }
        else
        {
            foreach (var t in order.Tenders)
            {
                var label = t.Type switch
                {
                    TenderType.Cash => "Cash",
                    TenderType.CardManual => "Card",
                    TenderType.CardIntegrated => "Card",
                    TenderType.PrepaidOnline => "Paid online",
                    TenderType.Voucher => "Voucher",
                    _ => t.Type.ToString(),
                };
                b.ColumnsAscii(label, EscPos.Money(t.Amount));
                if (t.CashReceived is > 0)
                    b.ColumnsAscii("  Tendered", EscPos.Money(t.CashReceived.Value));
                if (t.ChangeGiven is > 0)
                    b.ColumnsAscii("  Change", EscPos.Money(t.ChangeGiven.Value));
            }
            b.ColumnsAscii("Total paid", EscPos.Money(order.AmountPaid));
            if (interim)
                b.Large().Bold(true).ColumnsAscii("BALANCE DUE", EscPos.Money(order.BalanceDue), cols: 24).Bold(false).Normal();
            else
                b.Large().Bold(true).ColumnsAscii("PAID IN FULL", EscPos.Money(order.AmountPaid), cols: 24).Bold(false).Normal();
        }
        b.Separator('-').Center();
        if (interim)
        {
            b.Line("Not a final receipt");
        }
        else if (settings.ReceiptFooterLines.Count > 0)
        {
            foreach (var footer in settings.ReceiptFooterLines.Where(f => !string.IsNullOrWhiteSpace(f)))
                foreach (var wrapped in WrapLines(footer, EscPos.ColsFontA))
                    b.Line(wrapped);
        }
        else
        {
            b.Line("Thank you");
        }

        b.FeedAndCut();
        return b.Build();
    }

    /// <summary>
    /// The customer's proof that money went back.
    /// <para>
    /// Its own document, not a reprinted receipt with a minus sign. Someone
    /// holding this needs to see at a glance that it is a refund, what it was
    /// for, and how it was returned — and the shop's copy is what a manager
    /// matches against the drawer at close.
    /// </para>
    /// </summary>
    public static byte[] RenderRefund(
        PosOrder order,
        Refund refund,
        AppSettings settings,
        PrintDevice? device = null,
        IReadOnlyList<TaxClass>? taxClasses = null)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = NewBuilder(enc, settings.PrintChineseAsRaster, device);

        b.Center().KitchenLine(settings.ShopName, large: true);
        b.Normal().Line(settings.ShopAddress);
        b.Line($"{settings.ShopPostcode}  {settings.ShopPhone}");
        if (!string.IsNullOrWhiteSpace(settings.VatNumber))
            b.Line($"VAT No: {settings.VatNumber}");

        b.Bold(true).Line("*** REFUND ***").Bold(false).Left().Separator('=');
        b.Line($"Order {order.OrderNumber}");
        b.Line($"Sold  {order.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
        b.Line($"Refund {refund.At.ToLocalTime():dd/MM/yyyy HH:mm}");
        b.Separator();

        if (refund.Lines.Count > 0)
        {
            foreach (var line in refund.Lines)
                b.ItemEnglishAndPrice($"{line.Quantity} x {line.Name}", EscPos.Money(line.Amount));
            b.Separator();
        }

        b.Large().Bold(true)
            .ColumnsAscii("REFUNDED", EscPos.Money(refund.Amount), cols: 24)
            .Bold(false).Normal();

        WriteRefundVat(b, order, refund, settings, taxClasses);

        b.Separator('-');
        b.ColumnsAscii("Returned to", refund.Tender switch
        {
            TenderType.Cash => "Cash",
            TenderType.CardManual or TenderType.CardIntegrated => "Card",
            TenderType.PrepaidOnline => "Online",
            TenderType.Voucher => "Voucher",
            _ => refund.Tender.ToString(),
        });

        foreach (var wrapped in WrapLines($"Reason: {refund.Reason}", EscPos.ColsFontA))
            b.Line(wrapped);

        // What is left on the sale, so nobody has to work it out from two slips.
        var stillPaid = Money.Round(order.AmountPaid - order.AmountRefunded);
        b.ColumnsAscii("Originally paid", EscPos.Money(order.AmountPaid));
        b.ColumnsAscii("Kept on this sale", EscPos.Money(stillPaid));

        b.Separator('-').Center();
        b.Line("Please keep this receipt");
        b.FeedAndCut();
        return b.Build();
    }

    /// <summary>VAT going back out, at the rate the sale was made at.</summary>
    private static void WriteRefundVat(
        EscPosTicketBuilder b,
        PosOrder order,
        Refund refund,
        AppSettings settings,
        IReadOnlyList<TaxClass>? taxClasses)
    {
        if (string.IsNullOrWhiteSpace(settings.VatNumber) || taxClasses is not { Count: > 0 })
            return;

        var bands = TaxCalculator.SummariseRefund(
            order, refund, taxClasses, settings.DefaultTaxClassId, settings.PricesIncludeTax);
        if (bands.Count == 0) return;

        b.Separator('-');
        b.ColumnsAscii("Rate", "Net        VAT");
        foreach (var band in bands)
            b.ColumnsAscii(band.RateLabel, $"{EscPos.Money(band.Net),9}  {EscPos.Money(band.Vat),9}");

        b.ColumnsAscii("VAT refunded", EscPos.Money(TaxCalculator.TotalVat(bands)));
    }

    /// <summary>
    /// The VAT block. Silent unless the shop is registered — a receipt claiming
    /// VAT from a business that cannot charge it is worse than one that says
    /// nothing at all.
    /// </summary>
    private static void WriteVatSummary(
        EscPosTicketBuilder b,
        PosOrder order,
        AppSettings settings,
        IReadOnlyList<TaxClass>? taxClasses)
    {
        if (string.IsNullOrWhiteSpace(settings.VatNumber) || taxClasses is not { Count: > 0 })
            return;

        var bands = TaxCalculator.Summarise(
            order, taxClasses, settings.DefaultTaxClassId, settings.PricesIncludeTax);
        if (bands.Count == 0) return;

        b.Separator('-');
        b.ColumnsAscii("Rate", "Net        VAT");
        foreach (var band in bands)
            b.ColumnsAscii(band.RateLabel, $"{EscPos.Money(band.Net),9}  {EscPos.Money(band.Vat),9}");

        b.ColumnsAscii("Total VAT", EscPos.Money(TaxCalculator.TotalVat(bands)));
    }

    /// <summary>
    /// The X or Z reading — the one piece of paper an owner checks every night.
    /// <para>
    /// Laid out so the drawer block is last and reads downwards the way the
    /// count is actually done: what was in it, what went in, what came out,
    /// what should be there. Anyone reconciling a short drawer at eleven at
    /// night is following that column with a finger.
    /// </para>
    /// </summary>
    public static byte[] RenderShiftReport(
        ShiftReport report, AppSettings settings, PrintDevice? device = null)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = NewBuilder(enc, settings.PrintChineseAsRaster, device);

        b.Center().KitchenLine(
            string.IsNullOrWhiteSpace(settings.ShopName) ? "RingOrder EPOS" : settings.ShopName,
            large: true);
        b.Normal();
        if (!string.IsNullOrWhiteSpace(settings.ShopAddress)) b.Line(settings.ShopAddress);
        if (!string.IsNullOrWhiteSpace(settings.VatNumber)) b.Line($"VAT No: {settings.VatNumber}");

        b.Bold(true).KitchenLine(report.Title, large: true).Bold(false).Left().Separator('=');

        b.Line($"Shift    {report.ShiftNumber}");
        if (!string.IsNullOrWhiteSpace(report.TerminalId))
            b.Line($"Till     {report.TerminalId}");
        b.Line($"Opened   {report.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm}  {report.OpenedBy}");
        if (report.ClosedAt is { } closed)
            b.Line($"Closed   {closed.ToLocalTime():dd/MM/yyyy HH:mm}  {report.ClosedBy}");
        b.Line($"Printed  {report.PrintedAt.ToLocalTime():dd/MM/yyyy HH:mm}");

        // An X changes nothing, and saying so stops anyone treating a mid-shift
        // reading as the close.
        if (report.Kind == ShiftReportKind.X)
            b.Line("Shift is still open — this reading changes nothing");

        var t = report.Totals;

        // ── Sales ───────────────────────────────────────────────────────────
        b.Separator('=').Bold(true).Line("SALES").Bold(false);
        b.ColumnsAscii("Gross taken", EscPos.Money(t.GrossSales));
        if (t.HasRefunds)
        {
            b.ColumnsAscii($"Refunds ({t.RefundCount})", "-" + EscPos.Money(t.TotalRefunds));
            b.Bold(true).ColumnsAscii("Net kept", EscPos.Money(t.TotalTaken)).Bold(false);
        }

        if (report.DiscountCount > 0)
            b.ColumnsAscii($"Discounts ({report.DiscountCount})", "-" + EscPos.Money(report.Discounts));
        if (report.DeliveryFees > 0)
            b.ColumnsAscii("Delivery fees", EscPos.Money(report.DeliveryFees));
        if (report.Surcharges > 0)
            b.ColumnsAscii("Small-order fees", EscPos.Money(report.Surcharges));

        // ── Tenders ─────────────────────────────────────────────────────────
        b.Separator('-').Bold(true).Line("BY TENDER").Bold(false);
        foreach (var slice in report.ByTender)
            b.ColumnsAscii(slice.Label, EscPos.Money(slice.Amount));
        b.Bold(true).ColumnsAscii("Total taken", EscPos.Money(t.TotalTaken)).Bold(false);

        WriteSlices(b, "BY SERVICE TYPE", report.ByServiceType);
        WriteSlices(b, "BY CHANNEL", report.ByChannel);

        // ── VAT ─────────────────────────────────────────────────────────────
        // Same rule as the receipt: silent unless the shop is registered.
        if (!string.IsNullOrWhiteSpace(settings.VatNumber) && report.Vat.Count > 0)
        {
            b.Separator('-').Bold(true).Line("VAT").Bold(false);
            b.ColumnsAscii("Rate", "Net        VAT");
            foreach (var band in report.Vat)
                b.ColumnsAscii(band.RateLabel, $"{EscPos.Money(band.Net),9}  {EscPos.Money(band.Vat),9}");
            b.Bold(true).ColumnsAscii("Total VAT", EscPos.Money(report.TotalVat)).Bold(false);
        }

        // ── Orders ──────────────────────────────────────────────────────────
        b.Separator('-').Bold(true).Line("ORDERS").Bold(false);
        b.ColumnsAscii("Paid in full", t.OrdersPaid.ToString());
        b.ColumnsAscii("Value of settled sales", EscPos.Money(t.GrossPaid));
        if (t.OrdersOpen > 0)
        {
            b.ColumnsAscii("Still open", t.OrdersOpen.ToString());
            b.ColumnsAscii("Still owed", EscPos.Money(t.OutstandingDue));
        }
        if (t.OrdersVoided > 0)
            b.ColumnsAscii("Voided", t.OrdersVoided.ToString());

        // Takings and settled sales are different questions, and an owner who
        // does not know that will hunt for a fault that is not there. Said once,
        // plainly, and only when there is actually money on an open ticket.
        if (report.HasUnsettledMoney)
        {
            foreach (var wrapped in WrapLines(
                         "Takings exceed settled sales by whatever has been part-paid on tickets that are still open.",
                         EscPos.ColsFontA))
                b.Line(wrapped);
        }

        // ── The drawer ──────────────────────────────────────────────────────
        b.Separator('=').Bold(true).Line("DRAWER").Bold(false);
        b.ColumnsAscii("Opening float", EscPos.Money(t.OpeningFloat));
        b.ColumnsAscii("Cash sales", EscPos.Money(t.CashSales));
        if (t.CashRefunds > 0)
            b.Line($"  (after {EscPos.Money(t.CashRefunds)} cash refunded)");
        if (t.CashMovements != 0)
            b.ColumnsAscii("Pay in / pay out", EscPos.Money(t.CashMovements));

        b.Large().Bold(true)
            .ColumnsAscii("EXPECTED", EscPos.Money(t.ExpectedCash), cols: 24)
            .Bold(false).Normal();

        // Printed below the expected figure and never added into it. This money
        // is genuinely not in the drawer; a shift that looks short at eleven is
        // usually a driver who has not come back yet.
        if (report.CashOutWithDrivers > 0)
        {
            b.Separator('-');
            b.ColumnsAscii("Still out with drivers", EscPos.Money(report.CashOutWithDrivers));
            foreach (var driver in report.DriversOut)
                b.ColumnsAscii($"  {driver.Name} ({driver.Orders})", EscPos.Money(driver.CashHeld));
        }

        if (report.DeclaredCash is { } counted)
        {
            b.ColumnsAscii("Counted", EscPos.Money(counted));
            var variance = report.Variance ?? 0;
            var verdict = variance == 0
                ? "BALANCES"
                : variance > 0
                    ? $"OVER {EscPos.Money(variance)}"
                    : $"SHORT {EscPos.Money(-variance)}";
            b.Large().Bold(true).ColumnsAscii("VARIANCE", verdict, cols: 24).Bold(false).Normal();
        }

        b.Separator('=').Center();
        b.Line(report.Kind == ShiftReportKind.Z ? "End of shift" : "Shift continues");
        b.FeedAndCut();
        return b.Build();
    }

    private static void WriteSlices(EscPosTicketBuilder b, string heading, IReadOnlyList<ReportSlice> slices)
    {
        if (slices.Count == 0) return;
        b.Separator('-').Bold(true).Line(heading).Bold(false);
        foreach (var slice in slices)
            b.ColumnsAscii($"{slice.Label} ({slice.Count})", EscPos.Money(slice.Amount));
    }

    public static byte[] RenderTestPage(string printerName, AppSettings settings, PrintDevice? device = null)
    {
        var enc = string.IsNullOrWhiteSpace(settings.PrintEncoding) ? "gbk" : settings.PrintEncoding;
        var b = NewBuilder(enc, settings.PrintChineseAsRaster, device);
        b.Center().KitchenLine(string.IsNullOrWhiteSpace(settings.ShopName) ? "RingOrder EPOS" : settings.ShopName, large: true);
        b.Normal().Line("Printer test").Left().Separator();
        b.Line($"Printer: {printerName}");
        b.Line($"Encoding: {enc}  cols={EscPos.ColsFontA}");
        b.Line($"CJK raster: {settings.PrintChineseAsRaster}");
        b.Line(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        b.Separator();
        b.ItemEnglishAndPrice("1 x Sample: Optional Checkbox Extras", "2.00");
        b.KitchenLine("1 测试中文菜名", large: true);
        b.Normal().Line("  + Extra Rice");
        b.KitchenLine("  白饭", large: false);
        b.Separator();
        b.Large().ColumnsAscii("Total", "9.50", cols: 24).Normal();
        b.KitchenLine("Payment:CASH", large: true);
        b.Line("Cus Info:");
        b.KitchenLine("07700 900000", large: true);
        b.Line("12 Sample Street");
        b.Line("AB1 2CD");
        b.Separator();
        b.Center().Line("Printer test complete");
        b.FeedAndCut();
        return b.Build();
    }

    public static string NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outParts = new List<string>();
        foreach (var p in parts)
        {
            if (seen.Add(p)) outParts.Add(p);
        }
        return string.Join(", ", outParts);
    }

    /// <summary>
    /// The one line the kitchen reads first: what has to happen to this food.
    /// A collection order for someone standing at the counter says so, because
    /// that is the difference between cooking it now and cooking it next.
    /// </summary>
    public static string TicketHeadline(PosOrder order) => order.ServiceType switch
    {
        ServiceType.Delivery => "DELIVERY",
        ServiceType.EatIn => string.IsNullOrWhiteSpace(order.TableNumber)
            ? "EAT-IN"
            : $"TABLE {order.TableNumber}",
        _ => order.CustomerWaiting ? "COLLECTION - WAITING" : "COLLECTION",
    };

    /// <summary>
    /// Where it came from, when that is not the counter. Marketplace orders in
    /// particular have to be obvious: they are already paid and already timed.
    /// </summary>
    public static string? ChannelBanner(PosOrder order) => order.Channel switch
    {
        OrderChannel.Phone => "PHONE ORDER",
        OrderChannel.Web => "WEB ORDER",
        OrderChannel.Platform => string.IsNullOrWhiteSpace(order.PlatformName)
            ? "PLATFORM ORDER"
            : order.PlatformName!.ToUpperInvariant(),
        _ => null,
    };

    private static string? ResolvePayment(PosOrder order)
    {
        // Prefer live balance over stale PaymentLabel when partially paid
        if (order.HasPayments && !order.IsFullyPaid)
            return $"PART PAID DUE {EscPos.Money(order.BalanceDue)}";

        if (order.IsFullyPaid)
        {
            if (!string.IsNullOrWhiteSpace(order.PaymentLabel) &&
                !order.PaymentLabel.Contains("PART", StringComparison.OrdinalIgnoreCase) &&
                !order.PaymentLabel.Contains("DUE", StringComparison.OrdinalIgnoreCase))
                return order.PaymentLabel;

            if (order.Tenders.Count > 1)
                return "SPLIT";
            if (order.Tenders.Count > 0)
            {
                return order.Tenders[0].Type switch
                {
                    TenderType.Cash => "CASH",
                    TenderType.CardManual => "CARD",
                    TenderType.CardIntegrated => "CARD",
                    TenderType.PrepaidOnline => order.Tenders[0].Reference ?? "PAID ONLINE",
                    _ => order.Tenders[0].Type.ToString(),
                };
            }
            return order.PaymentLabel;
        }

        // Unpaid kitchen ticket — do not imply paid
        if (!string.IsNullOrWhiteSpace(order.PaymentLabel) &&
            order.PaymentLabel.Contains("PART", StringComparison.OrdinalIgnoreCase))
            return order.PaymentLabel;

        return order.Channel is OrderChannel.Web or OrderChannel.Platform ? "Order Not Paid" : "UNPAID";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<string> WrapLines(string? text, int width)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var s = text.Trim();
        while (s.Length > width)
        {
            var cut = s.LastIndexOf(' ', width);
            if (cut < width / 2) cut = width;
            yield return s[..cut].TrimEnd();
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }
}
