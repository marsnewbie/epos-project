namespace RingOrder.Epos.Domain;

/// <summary>
/// Which reading this is.
/// <para>
/// The difference is not the arithmetic — both count the same rows. It is what
/// the paper is for. An X is a look at the drawer with the shift still running;
/// a Z is the account of a session that has finished, and carries the count and
/// the variance because those only exist once someone has counted.
/// </para>
/// </summary>
public enum ShiftReportKind
{
    /// <summary>A reading taken mid-shift. Repeatable, changes nothing.</summary>
    X,

    /// <summary>The closing account of a finished shift.</summary>
    Z,
}

/// <summary>One line of a breakdown: how many, and how much.</summary>
public sealed record ReportSlice(string Label, int Count, decimal Amount);

/// <summary>
/// Everything an X or Z reading prints, worked out and ready to lay out.
/// <para>
/// Nothing here is stored. A shift's totals are summed from the rows carrying
/// its id, which is the same rule the rest of the till follows: no running
/// column can drift from the rows behind it.
/// </para>
/// <para>
/// One consequence to know about. A payment is written against the *order's*
/// shift, not the shift that was open when the money was taken — that is what
/// stops a reopened ticket moving yesterday's money into today. So settling an
/// old unpaid ticket adds to the shift it was rung up in, and a Z reprinted
/// afterwards will differ from the one that came out at close. The count, the
/// expected figure and the variance are frozen on the shift row and never move;
/// the sales figures are a live view of the rows.
/// </para>
/// </summary>
public sealed record ShiftReport(
    ShiftReportKind Kind,
    int ShiftNumber,
    string? TerminalId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string OpenedBy,
    string? ClosedBy,
    DateTimeOffset PrintedAt,
    ShiftTotals Totals,
    IReadOnlyList<ReportSlice> ByTender,
    IReadOnlyList<ReportSlice> ByServiceType,
    IReadOnlyList<ReportSlice> ByChannel,
    IReadOnlyList<TaxBand> Vat,
    int DiscountCount,
    decimal Discounts,
    decimal DeliveryFees,
    decimal Surcharges,
    decimal? DeclaredCash,
    decimal? Variance)
{
    public string Title => Kind == ShiftReportKind.Z ? "Z READING" : "X READING";

    /// <summary>
    /// True when money has been taken against tickets that are not settled, so
    /// the takings and the value of completed sales cannot match.
    /// <para>
    /// This is ordinary — a £20 ticket with £5 paid on it puts £5 in the drawer
    /// and nothing in "sales rung up" — but an owner comparing the two figures
    /// deserves to be told why rather than left hunting for a fault.
    /// </para>
    /// </summary>
    public bool HasUnsettledMoney => Totals.OrdersOpen > 0;

    public decimal TotalVat => Money.Round(Vat.Sum(v => v.Vat));
}

/// <summary>
/// The reading as plain text, for the screen and for a diagnostics export.
/// <para>
/// The paper version is separate because ESC/POS needs sizes and bold that mean
/// nothing here. What must not fork is the arithmetic, and it does not: both
/// render the same <see cref="ShiftReport"/>, so the figures on the screen are
/// the figures on the paper by construction rather than by discipline.
/// </para>
/// </summary>
public static class ShiftReportText
{
    public static string Render(ShiftReport r, bool showVat)
    {
        var t = r.Totals;
        var sb = new System.Text.StringBuilder();

        void Row(string label, string value) => sb.AppendLine($"{label,-24}{value,12}");
        void Money(string label, decimal amount) => Row(label, amount.ToString("0.00"));
        void Heading(string text) => sb.AppendLine().AppendLine(text).AppendLine(new string('-', 36));

        sb.AppendLine($"{r.Title} — shift {r.ShiftNumber}");
        sb.AppendLine($"Opened  {r.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm}  {r.OpenedBy}");
        if (r.ClosedAt is { } closed)
            sb.AppendLine($"Closed  {closed.ToLocalTime():dd/MM/yyyy HH:mm}  {r.ClosedBy}");
        if (r.Kind == ShiftReportKind.X)
            sb.AppendLine("Shift is still open — this reading changes nothing");

        Heading("SALES");
        Money("Gross taken", t.GrossSales);
        if (t.HasRefunds)
        {
            Money($"Refunds ({t.RefundCount})", -t.TotalRefunds);
            Money("Net kept", t.TotalTaken);
        }
        if (r.DiscountCount > 0) Money($"Discounts ({r.DiscountCount})", -r.Discounts);
        if (r.DeliveryFees > 0) Money("Delivery fees", r.DeliveryFees);
        if (r.Surcharges > 0) Money("Small-order fees", r.Surcharges);

        Heading("BY TENDER");
        foreach (var s in r.ByTender) Money(s.Label, s.Amount);
        Money("Total taken", t.TotalTaken);

        if (r.ByServiceType.Count > 0)
        {
            Heading("BY SERVICE TYPE");
            foreach (var s in r.ByServiceType) Money($"{s.Label} ({s.Count})", s.Amount);
        }

        if (r.ByChannel.Count > 0)
        {
            Heading("BY CHANNEL");
            foreach (var s in r.ByChannel) Money($"{s.Label} ({s.Count})", s.Amount);
        }

        if (showVat && r.Vat.Count > 0)
        {
            Heading("VAT");
            foreach (var band in r.Vat)
                sb.AppendLine($"{band.RateLabel,-10}net {band.Net,10:0.00}   VAT {band.Vat,10:0.00}");
            Money("Total VAT", r.TotalVat);
        }

        Heading("ORDERS");
        Row("Paid in full", t.OrdersPaid.ToString());
        Money("Value of settled sales", t.GrossPaid);
        if (t.OrdersOpen > 0)
        {
            Row("Still open", t.OrdersOpen.ToString());
            Money("Still owed", t.OutstandingDue);
        }
        if (t.OrdersVoided > 0) Row("Voided", t.OrdersVoided.ToString());

        if (r.HasUnsettledMoney)
        {
            sb.AppendLine("Takings exceed settled sales by whatever has been");
            sb.AppendLine("part-paid on tickets that are still open.");
        }

        Heading("DRAWER");
        Money("Opening float", t.OpeningFloat);
        Money("Cash sales", t.CashSales);
        if (t.CashRefunds > 0) sb.AppendLine($"  (after {t.CashRefunds:0.00} cash refunded)");
        if (t.CashMovements != 0) Money("Pay in / pay out", t.CashMovements);
        Money("EXPECTED IN DRAWER", t.ExpectedCash);

        if (r.DeclaredCash is { } counted)
        {
            Money("Counted", counted);
            var variance = r.Variance ?? 0;
            Row("VARIANCE", variance == 0
                ? "BALANCES"
                : variance > 0 ? $"OVER {variance:0.00}" : $"SHORT {-variance:0.00}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Builds a reading from the rows behind it. Pure, like <see cref="TaxCalculator"/>
/// and <see cref="RefundPolicy"/>, so the arithmetic on the one piece of paper an
/// owner checks every night can be tested without a database or a printer.
/// </summary>
public static class ShiftReportBuilder
{
    /// <param name="totals">
    /// Summed from <c>payments</c> and <c>cash_movements</c> carrying the shift id.
    /// </param>
    /// <param name="orders">
    /// The orders carrying the shift id — used for the breakdowns and the VAT,
    /// which cannot be answered from payment rows alone.
    /// </param>
    /// <param name="staffName">Resolves a staff id to a name for the header.</param>
    public static ShiftReport Build(
        Shift shift,
        ShiftTotals totals,
        IReadOnlyList<PosOrder> orders,
        IReadOnlyList<TaxClass> taxClasses,
        ShiftReportKind kind,
        Func<string?, string> staffName,
        string defaultTaxClassId = "hot-food",
        bool pricesIncludeTax = true,
        DateTimeOffset? printedAt = null)
    {
        // A void says the sale never happened, so it is counted on its own line
        // and kept out of every breakdown. Leaving voids in the service-type
        // split would inflate exactly the number an owner reads first.
        var sales = orders
            .Where(o => o.Status is not (PosOrderStatus.Voided or PosOrderStatus.Cancelled))
            .ToList();

        return new ShiftReport(
            Kind: kind,
            ShiftNumber: shift.Number,
            TerminalId: shift.TerminalId,
            OpenedAt: shift.OpenedAt,
            ClosedAt: shift.ClosedAt,
            OpenedBy: staffName(shift.OpenedByStaffId),
            ClosedBy: shift.ClosedByStaffId is null ? null : staffName(shift.ClosedByStaffId),
            PrintedAt: printedAt ?? DateTimeOffset.Now,
            Totals: totals,
            ByTender: TenderSlices(totals),
            ByServiceType: Slices(sales, o => ServiceTypeLabel(o.ServiceType), ServiceTypeOrder),
            ByChannel: Slices(sales, o => ChannelLabel(o.Channel), ChannelOrder),
            Vat: VatBands(sales, taxClasses, defaultTaxClassId, pricesIncludeTax),
            DiscountCount: sales.Count(o => o.DiscountTotal > 0),
            Discounts: Money.Round(sales.Sum(o => o.DiscountTotal)),
            DeliveryFees: Money.Round(sales.Sum(o => o.DeliveryFee)),
            Surcharges: Money.Round(sales.Sum(o => o.BelowMinimumSurcharge)),
            DeclaredCash: shift.DeclaredCash,
            Variance: shift.Variance);
    }

    /// <summary>
    /// Money by how it arrived. Taken from the shift totals rather than from the
    /// orders, because that is where the money actually is — a payment carries
    /// the shift it was taken in, and that is the figure the drawer must match.
    /// </summary>
    private static List<ReportSlice> TenderSlices(ShiftTotals totals)
    {
        var slices = new List<ReportSlice>
        {
            new("Cash", 0, totals.CashSales),
            new("Card", 0, totals.CardSales),
            new("Paid online", 0, totals.PrepaidSales),
            new("Voucher / other", 0, totals.OtherSales),
        };

        // A tender the shop never used is noise on a narrow roll. Cash always
        // stays: "cash £0.00" is information on a day nobody expected that.
        return slices.Where(s => s.Amount != 0 || s.Label == "Cash").ToList();
    }

    private static List<ReportSlice> Slices(
        IReadOnlyList<PosOrder> sales, Func<PosOrder, string> label, Func<string, int> order) =>
        sales
            .GroupBy(label)
            .Select(g => new ReportSlice(g.Key, g.Count(), Money.Round(g.Sum(o => o.Total))))
            .OrderBy(s => order(s.Label))
            .ToList();

    /// <summary>
    /// VAT for the session, summed per order rather than recomputed on the
    /// shift's gross.
    /// <para>
    /// This matters to the penny. VAT is rounded on each sale, because that is
    /// what the customer was charged and what their receipt says. Working it out
    /// again on the day's total would round once instead of two hundred times,
    /// and the shift report would disagree with the pile of receipts behind it by
    /// a few pence — which is exactly the discrepancy an accountant asks about.
    /// </para>
    /// <para>
    /// Refunds reverse at the rate the sale was made at and are netted off here,
    /// so the figure is what the shop actually owes on the session.
    /// </para>
    /// </summary>
    private static List<TaxBand> VatBands(
        IReadOnlyList<PosOrder> sales,
        IReadOnlyList<TaxClass> taxClasses,
        string defaultTaxClassId,
        bool pricesIncludeTax)
    {
        if (taxClasses.Count == 0) return [];

        var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var vat = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var gross = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var byId = new Dictionary<string, TaxClass>(StringComparer.Ordinal);

        void Add(TaxBand band, int sign)
        {
            byId[band.Class.Id] = band.Class;
            net[band.Class.Id] = net.GetValueOrDefault(band.Class.Id) + sign * band.Net;
            vat[band.Class.Id] = vat.GetValueOrDefault(band.Class.Id) + sign * band.Vat;
            gross[band.Class.Id] = gross.GetValueOrDefault(band.Class.Id) + sign * band.Gross;
        }

        foreach (var order in sales)
        {
            foreach (var band in TaxCalculator.Summarise(order, taxClasses, defaultTaxClassId, pricesIncludeTax))
                Add(band, +1);

            foreach (var refund in order.Refunds)
            foreach (var band in TaxCalculator.SummariseRefund(
                         order, refund, taxClasses, defaultTaxClassId, pricesIncludeTax))
                Add(band, -1);
        }

        return byId.Values
            .Select(c => new TaxBand(
                c,
                Money.Round(net.GetValueOrDefault(c.Id)),
                Money.Round(vat.GetValueOrDefault(c.Id)),
                Money.Round(gross.GetValueOrDefault(c.Id))))
            .Where(b => b.Gross != 0 || b.Vat != 0)
            .OrderByDescending(b => b.Class.RateBasisPoints)
            .ToList();
    }

    // The trade's words, matching what the buttons say — see INTERFACE.md. A
    // report that names things differently from the screen makes staff translate.
    private static string ServiceTypeLabel(ServiceType type) => type switch
    {
        ServiceType.Collection => "Collection",
        ServiceType.Delivery => "Delivery",
        ServiceType.EatIn => "Eat in",
        _ => type.ToString(),
    };

    private static string ChannelLabel(OrderChannel channel) => channel switch
    {
        OrderChannel.Counter => "Counter",
        OrderChannel.Phone => "Phone",
        OrderChannel.Web => "Web orders",
        OrderChannel.Platform => "Platform",
        _ => channel.ToString(),
    };

    private static int ServiceTypeOrder(string label) => label switch
    {
        "Collection" => 0, "Delivery" => 1, "Eat in" => 2, _ => 3,
    };

    private static int ChannelOrder(string label) => label switch
    {
        "Counter" => 0, "Phone" => 1, "Web orders" => 2, "Platform" => 3, _ => 4,
    };
}
