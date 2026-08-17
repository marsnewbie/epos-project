using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The X and Z readings — the one piece of paper an owner checks every night,
/// and the one an accountant asks about a year later.
/// </summary>
public class ShiftReportTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly OrderRepository _orders;
    private readonly ShiftRepository _shifts;
    private readonly StaffRepository _staff;
    private readonly RefundRepository _refunds;

    private static readonly List<TaxClass> Bands =
    [
        new() { Id = "hot-food", Name = "Hot food", RateBasisPoints = 2000 },
        new() { Id = "cold-food", Name = "Cold food", RateBasisPoints = 0 },
    ];

    public ShiftReportTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _orders = new OrderRepository(_db);
        _shifts = new ShiftRepository(_db);
        _staff = new StaffRepository(_db);
        _refunds = new RefundRepository(_db);
    }

    private StaffMember AddStaff(string name = "Wei")
    {
        var (hash, salt) = PinHasher.Hash(Guid.NewGuid().ToString("N")[..6]);
        var member = new StaffMember { Name = name, Role = StaffRole.Manager, PinHash = hash, PinSalt = salt };
        _staff.Upsert(member);
        return member;
    }

    private PosOrder Sale(
        Shift shift, StaffMember staff, decimal amount,
        ServiceType service = ServiceType.Collection,
        OrderChannel channel = OrderChannel.Counter,
        string taxClassId = "hot-food",
        PosOrderStatus? status = null,
        params OrderTender[] tenders)
    {
        var order = new PosOrder
        {
            OrderNumber = Guid.NewGuid().ToString("N")[..8],
            ShiftId = shift.Id,
            StaffId = staff.Id,
            Channel = channel,
            ServiceType = service,
            Status = status ?? PosOrderStatus.Paid,
            Lines =
            [
                new CartLine
                {
                    Name = "Test dish", BasePrice = amount, Quantity = 1,
                    IsAdHoc = true, TaxClassId = taxClassId,
                },
            ],
            Tenders = tenders.Length > 0
                ? tenders.ToList()
                : [new OrderTender { Type = TenderType.Cash, Amount = amount, StaffId = staff.Id }],
        };
        // A payment inherits the order's shift on write — see OrderRepository.
        _orders.Upsert(order);
        return order;
    }

    private ShiftReport Report(Shift shift, ShiftReportKind kind = ShiftReportKind.X) =>
        ShiftReportBuilder.Build(
            shift,
            _shifts.GetTotals(shift),
            _orders.GetForShift(shift.Id),
            Bands,
            kind,
            id => _staff.GetById(id ?? "")?.Name ?? "—");

    [Fact]
    public void Gross_refunds_and_net_are_three_separate_numbers()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 100m);
        var order = Sale(shift, staff, 60m);

        _refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id, StaffId = staff.Id,
            Amount = 15m, Tender = TenderType.Cash, Reason = "Wrong dish",
        });

        var report = Report(shift);

        // "Took £60 and gave £15 back" is a different conversation from
        // "took £45", and only one of them tells an owner to go and look.
        Assert.Equal(60m, report.Totals.GrossSales);
        Assert.Equal(15m, report.Totals.TotalRefunds);
        Assert.Equal(45m, report.Totals.TotalTaken);
        Assert.Equal(1, report.Totals.RefundCount);
    }

    [Fact]
    public void A_cash_refund_comes_out_of_the_drawer()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 100m);
        var order = Sale(shift, staff, 60m);

        _refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id, StaffId = staff.Id,
            Amount = 15m, Tender = TenderType.Cash, Reason = "Wrong dish",
        });

        // 100 float + 60 taken - 15 handed back.
        Assert.Equal(145m, Report(shift).Totals.ExpectedCash);
    }

    [Fact]
    public void A_voided_sale_is_counted_but_kept_out_of_every_breakdown()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        Sale(shift, staff, 20m, service: ServiceType.Delivery);
        Sale(shift, staff, 99m, service: ServiceType.Delivery,
            status: PosOrderStatus.Voided,
            tenders: new OrderTender { Type = TenderType.Cash, Amount = 0m });

        var report = Report(shift);

        Assert.Equal(1, report.Totals.OrdersVoided);

        // A void says the sale never happened. Leaving it in would inflate
        // exactly the figure an owner reads first.
        var delivery = Assert.Single(report.ByServiceType, s => s.Label == "Delivery");
        Assert.Equal(1, delivery.Count);
        Assert.Equal(20m, delivery.Amount);
    }

    [Fact]
    public void Service_type_and_channel_are_split_separately()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        Sale(shift, staff, 10m, service: ServiceType.Collection, channel: OrderChannel.Counter);
        Sale(shift, staff, 20m, service: ServiceType.Delivery, channel: OrderChannel.Phone);
        Sale(shift, staff, 30m, service: ServiceType.Delivery, channel: OrderChannel.Web);

        var report = Report(shift);

        // Two independent axes: a phone order can be either service type, and a
        // web order can too. Squashing them into one list is the mistake.
        Assert.Equal(50m, Assert.Single(report.ByServiceType, s => s.Label == "Delivery").Amount);
        Assert.Equal(2, Assert.Single(report.ByServiceType, s => s.Label == "Delivery").Count);
        Assert.Equal(20m, Assert.Single(report.ByChannel, s => s.Label == "Phone").Amount);
        Assert.Equal(30m, Assert.Single(report.ByChannel, s => s.Label == "Web orders").Amount);
    }

    [Fact]
    public void Vat_is_summed_per_order_so_the_report_agrees_with_the_receipts()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);

        // Three sales whose VAT each rounds up a half-penny. Worked out again on
        // the day's total this would round once instead of three times, and the
        // report would disagree with the pile of receipts behind it.
        for (var i = 0; i < 3; i++) Sale(shift, staff, 6.13m);

        var report = Report(shift);
        var perOrder = Money.Round(_orders.GetForShift(shift.Id)
            .Sum(o => TaxCalculator.TotalVat(TaxCalculator.Summarise(o, Bands))));

        Assert.Equal(perOrder, report.TotalVat);
    }

    [Fact]
    public void Refunded_vat_comes_back_off_the_session()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        var order = Sale(shift, staff, 120m);

        var beforeRefund = Report(shift).TotalVat;

        _refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id, StaffId = staff.Id,
            Amount = 60m, Tender = TenderType.Cash, Reason = "Half the order was wrong",
        });

        // Half the sale went back, so half the VAT did too.
        Assert.Equal(Money.Round(beforeRefund / 2), Report(shift).TotalVat);
    }

    [Fact]
    public void Zero_rated_and_standard_rated_stay_in_their_own_bands()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        Sale(shift, staff, 60m, taxClassId: "hot-food");
        Sale(shift, staff, 40m, taxClassId: "cold-food");

        var report = Report(shift);

        Assert.Equal(2, report.Vat.Count);
        Assert.Equal(10m, Assert.Single(report.Vat, b => b.Class.Id == "hot-food").Vat);
        Assert.Equal(0m, Assert.Single(report.Vat, b => b.Class.Id == "cold-food").Vat);
    }

    [Fact]
    public void An_x_reading_has_no_count_and_no_variance()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 50m);
        Sale(shift, staff, 10m);

        var x = Report(shift, ShiftReportKind.X);

        // Nothing has been counted yet, so there is nothing honest to print.
        Assert.Null(x.DeclaredCash);
        Assert.Null(x.Variance);
        Assert.Equal("X READING", x.Title);
    }

    [Fact]
    public void A_z_reading_carries_the_count_and_the_variance()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 50m);
        Sale(shift, staff, 10m);

        var totals = _shifts.GetTotals(shift);
        _shifts.Close(shift, staff.Id, declaredCash: 58m, expectedCash: totals.ExpectedCash, notes: null);

        var z = Report(shift, ShiftReportKind.Z);

        Assert.Equal("Z READING", z.Title);
        Assert.Equal(58m, z.DeclaredCash);
        Assert.Equal(-2m, z.Variance);
        Assert.NotNull(z.ClosedAt);
    }

    /// <summary>
    /// The reason there is no snapshot table and no migration behind any of
    /// this: the same rows always produce the same reading, so a reprint is the
    /// same paper. See <see cref="Settling_an_old_ticket_lands_in_the_shift_it_was_rung_up_in"/>
    /// for the one way those rows can still change after a close.
    /// </summary>
    [Fact]
    public void The_same_rows_always_produce_the_same_reading()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 40m);
        Sale(shift, staff, 25m);
        Sale(shift, staff, 12.50m, channel: OrderChannel.Phone);
        _shifts.Close(shift, staff.Id, 77.50m, _shifts.GetTotals(shift).ExpectedCash, null);

        var first = Report(shift, ShiftReportKind.Z);
        var second = Report(shift, ShiftReportKind.Z);

        Assert.Equal(first.Totals, second.Totals);
        Assert.Equal(first.TotalVat, second.TotalVat);
        Assert.Equal(first.Variance, second.Variance);
        Assert.Equal(
            ShiftReportText.Render(first with { PrintedAt = second.PrintedAt }, showVat: true),
            ShiftReportText.Render(second, showVat: true));
    }

    [Fact]
    public void Discounts_delivery_and_small_order_fees_each_get_their_own_line()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);

        var order = Sale(shift, staff, 30m, service: ServiceType.Delivery);
        order.DiscountTotal = 5m;
        order.DeliveryFee = 2.50m;
        order.BelowMinimumSurcharge = 1.50m;
        _orders.Upsert(order);

        var report = Report(shift);

        Assert.Equal(5m, report.Discounts);
        Assert.Equal(1, report.DiscountCount);
        Assert.Equal(2.50m, report.DeliveryFees);
        Assert.Equal(1.50m, report.Surcharges);
    }

    /// <summary>
    /// Money on a ticket nobody has settled is in the drawer but is not a sale
    /// yet, so the two figures cannot match. The report says why instead of
    /// leaving an owner to hunt for a fault.
    /// </summary>
    [Fact]
    public void Part_payment_puts_money_in_the_drawer_without_making_a_sale()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);

        Sale(shift, staff, 20m, status: PosOrderStatus.Sent,
            tenders: new OrderTender { Type = TenderType.Cash, Amount = 5m });

        var report = Report(shift);

        Assert.Equal(5m, report.Totals.TotalTaken);   // in the drawer
        Assert.Equal(0m, report.Totals.GrossPaid);    // not a completed sale
        Assert.Equal(15m, report.Totals.OutstandingDue);
        Assert.True(report.HasUnsettledMoney);
    }

    /// <summary>
    /// A payment is written against the <em>order's</em> shift, not the shift
    /// that was open when the money was taken — which is what stops a reopened
    /// ticket moving yesterday's money into today.
    /// <para>
    /// The consequence, pinned down here because it is surprising: settling an
    /// old ticket adds to the shift it was rung up in, so a Z reprinted later
    /// differs from the one that came out at close. The counted cash and the
    /// variance are frozen on the shift row and never move.
    /// </para>
    /// </summary>
    [Fact]
    public void Settling_an_old_ticket_lands_in_the_shift_it_was_rung_up_in()
    {
        var staff = AddStaff();
        var first = _shifts.Open(staff.Id, 0m);

        var order = Sale(first, staff, 20m, status: PosOrderStatus.Sent,
            tenders: new OrderTender { Type = TenderType.Cash, Amount = 0m });
        var atClose = _shifts.GetTotals(first);
        _shifts.Close(first, staff.Id, 0m, atClose.ExpectedCash, null);

        var second = _shifts.Open(staff.Id, 0m);
        order.Tenders = [new OrderTender { Type = TenderType.Cash, Amount = 20m, StaffId = staff.Id }];
        order.Status = PosOrderStatus.Paid;
        _orders.Upsert(order);

        // Not in the shift that was open when it was paid...
        Assert.Equal(0m, Report(second).Totals.TotalTaken);

        // ...but added to the closed one, whose sales figures are a live view.
        var reprint = Report(first, ShiftReportKind.Z);
        Assert.Equal(20m, reprint.Totals.TotalTaken);

        // The count and the variance are frozen and do not follow.
        Assert.Equal(0m, reprint.DeclaredCash);
        Assert.Equal(atClose.ExpectedCash, reprint.Totals.OpeningFloat + 0m);
    }

    [Fact]
    public void Cash_is_always_listed_even_when_nothing_was_taken_in_it()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        Sale(shift, staff, 30m, tenders: new OrderTender { Type = TenderType.CardManual, Amount = 30m });

        var report = Report(shift);

        // "Cash £0.00" is information on a day nobody expected that. An unused
        // tender the shop never touched is just noise on a narrow roll.
        Assert.Contains(report.ByTender, s => s.Label == "Cash" && s.Amount == 0m);
        Assert.DoesNotContain(report.ByTender, s => s.Label == "Voucher / other");
    }

    [Fact]
    public void A_report_route_ignores_service_type_and_channel()
    {
        var device = new PrintDevice { Id = "front", Name = "Counter", IsEnabled = true };
        var devices = new Dictionary<string, PrintDevice> { ["front"] = device };

        // Written about tickets, not about readings. Applying it here would
        // silently swallow the one document an owner prints by hand.
        var routes = new List<PrintRoute>
        {
            new()
            {
                Document = PrintDocument.Report, DeviceId = "front",
                ServiceType = ServiceType.Delivery, Channel = OrderChannel.Web,
            },
        };

        var targets = PrintRouting.RouteStandalone(PrintDocument.Report, routes, devices);
        Assert.Single(targets);
        Assert.Equal("front", targets[0].Device.Id);
    }

    [Fact]
    public void A_kitchen_route_never_catches_the_reading()
    {
        var device = new PrintDevice { Id = "wok", Name = "Wok", IsEnabled = true };
        var devices = new Dictionary<string, PrintDevice> { ["wok"] = device };
        var routes = new List<PrintRoute>
        {
            new() { Document = PrintDocument.Kitchen, DeviceId = "wok" },
        };

        Assert.Empty(PrintRouting.RouteStandalone(PrintDocument.Report, routes, devices));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }
}
