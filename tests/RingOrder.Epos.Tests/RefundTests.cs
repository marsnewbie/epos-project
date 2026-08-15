using Microsoft.Data.Sqlite;
using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Money going back out.
/// <para>
/// A refund is the one action that moves money outwards on a member of staff's
/// say-so, so the rules that refuse one matter as much as the arithmetic. The
/// principle underneath all of it: the sale is never rewritten, because the shop
/// has to be able to show both what was sold and what was returned.
/// </para>
/// </summary>
public class RefundTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-refund-{Guid.NewGuid():N}.sqlite");

    private static readonly TaxClass Standard = new()
        { Id = "hot-food", Name = "Hot food", RateBasisPoints = 2000 };
    private static readonly TaxClass Zero = new()
        { Id = "cold-food", Name = "Cold food", RateBasisPoints = 0 };
    private static readonly TaxClass[] Classes = [Standard, Zero];

    // ── What may be refunded ────────────────────────────────────────────────

    [Fact]
    public void Only_what_was_actually_taken_can_go_back()
    {
        // A £20 order with £5 paid can return £5, not £20. The order total is
        // not the ceiling — the money received is.
        var order = Paid(total: 20m, paid: 5m);

        Assert.Equal(5m, RefundPolicy.Refundable(order));
        Assert.NotNull(RefundPolicy.Validate(order, 20m, "wrong dish"));
        Assert.Null(RefundPolicy.Validate(order, 5m, "wrong dish"));
    }

    [Fact]
    public void A_second_refund_can_only_take_what_the_first_one_left()
    {
        var order = Paid(total: 20m, paid: 20m);
        order.Refunds.Add(new Refund { Amount = 8m, Reason = "cold" });

        Assert.Equal(12m, RefundPolicy.Refundable(order));
        Assert.NotNull(RefundPolicy.Validate(order, 12.01m, "more"));
        Assert.Null(RefundPolicy.Validate(order, 12m, "the rest"));
    }

    [Fact]
    public void An_order_already_refunded_in_full_refuses_another()
    {
        var order = Paid(total: 20m, paid: 20m);
        order.Refunds.Add(new Refund { Amount = 20m, Reason = "all of it" });

        Assert.Equal(0m, RefundPolicy.Refundable(order));
        Assert.Contains("full", RefundPolicy.Validate(order, 1m, "again")!, StringComparison.OrdinalIgnoreCase);
        Assert.True(order.IsFullyRefunded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Nothing_and_less_than_nothing_are_refused(decimal amount) =>
        Assert.NotNull(RefundPolicy.Validate(Paid(20m, 20m), amount, "why"));

    [Fact]
    public void A_refund_without_a_reason_is_refused()
    {
        // An unexplained refund is an unexplained hole in the takings.
        Assert.NotNull(RefundPolicy.Validate(Paid(20m, 20m), 5m, ""));
        Assert.NotNull(RefundPolicy.Validate(Paid(20m, 20m), 5m, "   "));
        Assert.Null(RefundPolicy.Validate(Paid(20m, 20m), 5m, "dropped it"));
    }

    [Fact]
    public void An_unpaid_order_has_nothing_to_refund()
    {
        var order = new PosOrder { Total = 20m, Status = PosOrderStatus.Sent };
        Assert.NotNull(RefundPolicy.Validate(order, 5m, "why"));
    }

    [Fact]
    public void A_voided_order_has_nothing_to_refund()
    {
        var order = Paid(20m, 20m);
        order.Status = PosOrderStatus.Voided;
        Assert.NotNull(RefundPolicy.Validate(order, 5m, "why"));
    }

    [Fact]
    public void A_full_refund_built_from_lines_is_not_refused_by_a_rounding_crumb()
    {
        // Three lines at 6.67 sum to 20.01 against 20.00 taken. Refusing that
        // would leave staff unable to refund an order they can see is fully paid.
        var order = Paid(total: 20m, paid: 20m);
        Assert.Null(RefundPolicy.Validate(order, 20.004m, "all of it"));
    }

    [Fact]
    public void The_suggested_tender_is_the_one_most_of_the_money_came_in_on()
    {
        // Cash back on a card sale is the shape of most till fraud, so it is a
        // deliberate change rather than the default.
        var order = new PosOrder
        {
            Total = 30m,
            Tenders =
            [
                new OrderTender { Type = TenderType.Cash, Amount = 5m },
                new OrderTender { Type = TenderType.CardManual, Amount = 25m },
            ],
        };

        Assert.Equal(TenderType.CardManual, RefundPolicy.SuggestTender(order));
    }

    [Fact]
    public void A_line_already_handed_back_is_not_offered_twice()
    {
        var order = Paid(20m, 20m);
        order.Lines =
        [
            new CartLine { Id = "l1", Name = "Curry", Quantity = 1, LineTotal = 8m },
            new CartLine { Id = "l2", Name = "Rice", Quantity = 1, LineTotal = 3m },
        ];
        order.Refunds.Add(new Refund
        {
            Amount = 8m,
            Reason = "cold",
            Lines = [new RefundLine { LineId = "l1", Name = "Curry", Quantity = 1, Amount = 8m }],
        });

        var offered = RefundPolicy.RefundableLines(order);

        Assert.Equal("Rice", Assert.Single(offered).Name);
    }

    // ── The sale is never rewritten ─────────────────────────────────────────

    [Fact]
    public void A_refund_does_not_make_the_order_look_unpaid_again()
    {
        // If a refund loaded as a negative tender, AmountPaid would fall, the
        // balance would reappear, and the till would offer to settle a sale that
        // was already settled and then reversed.
        var order = Paid(20m, 20m);
        order.Refunds.Add(new Refund { Amount = 20m, Reason = "all of it" });

        Assert.Equal(20m, order.AmountPaid);
        Assert.Equal(0m, order.BalanceDue);
        Assert.True(order.IsFullyPaid);
        Assert.Equal(0m, order.NetTaken);
    }

    [Fact]
    public void Re_saving_an_order_does_not_erase_its_refunds()
    {
        // Refund rows live in `payments` too, and saving an order rewrites that
        // table. Money already handed back is not the caller's to delete.
        var (db, orders, refunds) = NewFixture();

        var order = Paid(20m, 20m);
        orders.Upsert(order);
        refunds.Record(new Refund
        {
            OrderId = order.Id, Amount = 7.50m, Tender = TenderType.Cash, Reason = "wrong dish",
        });

        orders.Upsert(order);           // an ordinary re-save, e.g. after printing

        var reloaded = orders.GetToday().Single(o => o.Id == order.Id);
        Assert.Equal(7.50m, reloaded.AmountRefunded);
        Assert.Equal(20m, reloaded.AmountPaid);
        Assert.Equal(12.50m, reloaded.NetTaken);
    }

    [Fact]
    public void A_refund_survives_a_reload_with_its_reason_and_its_lines()
    {
        var (db, orders, refunds) = NewFixture();

        var order = Paid(20m, 20m);
        order.Lines = [new CartLine { Id = "l1", Name = "Curry", Quantity = 1, LineTotal = 8m }];
        orders.Upsert(order);

        refunds.Record(new Refund
        {
            OrderId = order.Id,
            Amount = 8m,
            Tender = TenderType.CardManual,
            Reason = "arrived cold",
            Lines = [new RefundLine { LineId = "l1", Name = "Curry", Quantity = 1, Amount = 8m }],
        });

        var reloaded = orders.GetToday().Single(o => o.Id == order.Id).Refunds.Single();

        Assert.Equal(8m, reloaded.Amount);
        Assert.Equal(TenderType.CardManual, reloaded.Tender);
        Assert.Equal("arrived cold", reloaded.Reason);
        Assert.Equal("Curry", reloaded.Lines.Single().Name);
    }

    // ── The drawer and the shift ────────────────────────────────────────────

    [Fact]
    public void A_cash_refund_comes_out_of_the_expected_drawer()
    {
        var (db, orders, refunds) = NewFixture();
        var shifts = new ShiftRepository(db);
        var shift = OpenShift(shifts, float_: 100m);

        var order = Paid(20m, 20m);
        order.ShiftId = shift.Id;
        orders.Upsert(order);
        StampPaymentsToShift(db, order.Id, shift.Id);

        var before = shifts.GetTotals(shift);
        Assert.Equal(120m, before.ExpectedCash);

        refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id,
            Amount = 7.50m, Tender = TenderType.Cash, Reason = "wrong dish",
        });

        var after = shifts.GetTotals(shift);

        Assert.Equal(112.50m, after.ExpectedCash);   // money left the drawer
        Assert.Equal(7.50m, after.CashRefunds);
        Assert.Equal(1, after.RefundCount);
    }

    [Fact]
    public void A_card_refund_leaves_the_drawer_alone()
    {
        var (db, orders, refunds) = NewFixture();
        var shifts = new ShiftRepository(db);
        var shift = OpenShift(shifts, float_: 100m);

        var order = Paid(20m, 20m);
        order.ShiftId = shift.Id;
        orders.Upsert(order);
        StampPaymentsToShift(db, order.Id, shift.Id);

        refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id,
            Amount = 5m, Tender = TenderType.CardManual, Reason = "overcharged",
        });

        var totals = shifts.GetTotals(shift);

        Assert.Equal(120m, totals.ExpectedCash);
        Assert.Equal(0m, totals.CashRefunds);
        Assert.Equal(5m, totals.NonCashRefunds);
    }

    [Fact]
    public void The_report_shows_what_was_taken_and_what_went_back()
    {
        // "We took £20 and refunded £7.50" is a different conversation from
        // "we took £12.50", and only one of them tells a manager to go and look.
        var (db, orders, refunds) = NewFixture();
        var shifts = new ShiftRepository(db);
        var shift = OpenShift(shifts, float_: 0m);

        var order = Paid(20m, 20m);
        order.ShiftId = shift.Id;
        orders.Upsert(order);
        StampPaymentsToShift(db, order.Id, shift.Id);

        refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id,
            Amount = 7.50m, Tender = TenderType.Cash, Reason = "wrong dish",
        });

        var totals = shifts.GetTotals(shift);

        Assert.Equal(20m, totals.GrossSales);
        Assert.Equal(7.50m, totals.TotalRefunds);
        Assert.Equal(12.50m, totals.TotalTaken);
    }

    [Fact]
    public void A_refunded_sale_still_counts_as_a_paid_order()
    {
        // It was a sale. Counting the refund against what was settled would drop
        // the order back among the unpaid and invent money the shop is owed.
        var (db, orders, refunds) = NewFixture();
        var shifts = new ShiftRepository(db);
        var shift = OpenShift(shifts, float_: 0m);

        var order = Paid(20m, 20m);
        order.ShiftId = shift.Id;
        orders.Upsert(order);
        StampPaymentsToShift(db, order.Id, shift.Id);

        refunds.Record(new Refund
        {
            OrderId = order.Id, ShiftId = shift.Id,
            Amount = 20m, Tender = TenderType.Cash, Reason = "all of it",
        });

        var totals = shifts.GetTotals(shift);

        Assert.Equal(1, totals.OrdersPaid);
        Assert.Equal(0, totals.OrdersOpen);
        Assert.Equal(0m, totals.OutstandingDue);
    }

    // ── VAT ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Refunding_a_line_reverses_that_lines_own_rate()
    {
        var order = Paid(20m, 20m);
        var refund = new Refund
        {
            Amount = 6m,
            Reason = "cold",
            Lines = [new RefundLine { Name = "Curry", Quantity = 1, Amount = 6m, TaxClassId = "hot-food" }],
        };

        var band = TaxCalculator.SummariseRefund(order, refund, Classes).Single();

        Assert.Equal(5m, band.Net);
        Assert.Equal(1m, band.Vat);
        Assert.Equal("20%", band.RateLabel);
    }

    [Fact]
    public void Refunding_a_zero_rated_line_gives_back_no_vat()
    {
        var order = Paid(20m, 20m);
        var refund = new Refund
        {
            Amount = 4m,
            Reason = "cold drink",
            Lines = [new RefundLine { Name = "Water", Quantity = 1, Amount = 4m, TaxClassId = "cold-food" }],
        };

        Assert.Equal(0m, TaxCalculator.TotalVat(TaxCalculator.SummariseRefund(order, refund, Classes)));
    }

    [Fact]
    public void An_amount_refund_reverses_vat_in_the_orders_own_proportions()
    {
        // Half hot, half cold; refund half the order. Half the VAT goes back —
        // not the standard rate on the whole amount, and not none of it.
        var order = new PosOrder
        {
            Lines =
            [
                new CartLine { Name = "Curry", Quantity = 1, BasePrice = 12m, LineTotal = 12m, TaxClassId = "hot-food", IsAdHoc = true },
                new CartLine { Name = "Water", Quantity = 1, BasePrice = 12m, LineTotal = 12m, TaxClassId = "cold-food", IsAdHoc = true },
            ],
            Subtotal = 24m,
            Total = 24m,
            Tenders = [new OrderTender { Type = TenderType.Cash, Amount = 24m }],
            Status = PosOrderStatus.Paid,
        };

        var full = TaxCalculator.TotalVat(TaxCalculator.Summarise(order, Classes));
        var half = TaxCalculator.TotalVat(TaxCalculator.SummariseRefund(
            order, new Refund { Amount = 12m, Reason = "half" }, Classes));

        Assert.Equal(2m, full);        // £12 hot at 20% = £2
        Assert.Equal(1m, half);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// A settled sale with a line behind it. The line is not decoration: saving
    /// an order recalculates its totals from its lines, so a total with nothing
    /// underneath it becomes zero the moment it is written.
    /// </summary>
    private static PosOrder Paid(decimal total, decimal paid) => new()
    {
        OrderNumber = "T-001",
        Lines =
        [
            new CartLine
            {
                Id = "sale", Name = "Meal", Quantity = 1,
                BasePrice = total, LineTotal = total,
                TaxClassId = "hot-food", IsAdHoc = true,
            }
        ],
        Total = total,
        Subtotal = total,
        Status = PosOrderStatus.Paid,
        Tenders = paid > 0 ? [new OrderTender { Type = TenderType.Cash, Amount = paid }] : [],
    };

    private static Shift OpenShift(ShiftRepository shifts, decimal float_)
    {
        var shift = new Shift
        {
            Number = 1,
            Status = ShiftStatus.Open,
            OpenedByStaffId = "staff-1",
            OpeningFloat = float_,
        };
        shifts.Upsert(shift);
        return shift;
    }

    /// <summary>
    /// Puts the sale's payments into the shift. The till does this through the
    /// session; here it is done directly so the totals under test are the
    /// repository's arithmetic and nothing else.
    /// </summary>
    private static void StampPaymentsToShift(EposDb db, string orderId, string shiftId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE payments SET shift_id=$s WHERE order_id=$o";
        cmd.Parameters.AddWithValue("$s", shiftId);
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.ExecuteNonQuery();
    }

    private (EposDb Db, OrderRepository Orders, RefundRepository Refunds) NewFixture()
    {
        var db = new EposDb(_dbPath);
        db.Migrate();
        return (db, new OrderRepository(db), new RefundRepository(db));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* the OS will get it */ }
    }
}
