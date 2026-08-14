using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Orders, payments and shift totals. These are the sums a merchant checks
/// against the cash in the drawer, so they are checked here first.
/// </summary>
public class ShiftAndPaymentTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly OrderRepository _orders;
    private readonly ShiftRepository _shifts;
    private readonly StaffRepository _staff;

    public ShiftAndPaymentTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _orders = new OrderRepository(_db);
        _shifts = new ShiftRepository(_db);
        _staff = new StaffRepository(_db);
    }

    private StaffMember AddStaff(string name = "Wei", StaffRole role = StaffRole.Cashier)
    {
        var (hash, salt) = PinHasher.Hash("4321");
        var member = new StaffMember { Name = name, Role = role, PinHash = hash, PinSalt = salt };
        _staff.Upsert(member);
        return member;
    }

    private PosOrder Sale(Shift shift, StaffMember staff, decimal amount, params OrderTender[] tenders)
    {
        var order = new PosOrder
        {
            OrderNumber = Guid.NewGuid().ToString("N")[..8],
            ShiftId = shift.Id,
            StaffId = staff.Id,
            Channel = OrderChannel.Counter,
            ServiceType = ServiceType.Collection,
            Status = tenders.Sum(t => t.Amount) >= amount ? PosOrderStatus.Paid : PosOrderStatus.Sent,
            Lines =
            [
                new CartLine { Name = "Test dish", BasePrice = amount, Quantity = 1, IsAdHoc = true },
            ],
            Tenders = tenders.ToList(),
        };
        _orders.Upsert(order);
        return order;
    }

    [Fact]
    public void Lines_and_payments_round_trip_through_their_own_tables()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 50m);
        var order = Sale(shift, staff, 12.40m,
            new OrderTender { Type = TenderType.Cash, Amount = 12.40m, CashReceived = 20m, ChangeGiven = 7.60m });

        var loaded = _orders.GetById(order.Id)!;

        Assert.Single(loaded.Lines);
        Assert.Equal(12.40m, loaded.Total);
        Assert.Single(loaded.Tenders);
        Assert.Equal(20m, loaded.Tenders[0].CashReceived);
        Assert.Equal(7.60m, loaded.Tenders[0].ChangeGiven);
        Assert.True(loaded.IsFullyPaid);
    }

    [Fact]
    public void Partial_payment_leaves_a_balance_due()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        var order = Sale(shift, staff, 20m,
            new OrderTender { Type = TenderType.Cash, Amount = 5m });

        var loaded = _orders.GetById(order.Id)!;
        Assert.False(loaded.IsFullyPaid);
        Assert.Equal(15m, loaded.BalanceDue);
    }

    [Fact]
    public void Split_tender_settles_the_order()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        var order = Sale(shift, staff, 30m,
            new OrderTender { Type = TenderType.Cash, Amount = 10m },
            new OrderTender { Type = TenderType.CardManual, Amount = 20m });

        var loaded = _orders.GetById(order.Id)!;
        Assert.True(loaded.IsFullyPaid);
        Assert.Equal(0m, loaded.BalanceDue);
    }

    [Fact]
    public void Expected_cash_is_float_plus_cash_taken_plus_pay_ins()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 100m);

        Sale(shift, staff, 25m, new OrderTender { Type = TenderType.Cash, Amount = 25m });
        Sale(shift, staff, 40m, new OrderTender { Type = TenderType.CardManual, Amount = 40m });
        Sale(shift, staff, 18m, new OrderTender { Type = TenderType.PrepaidOnline, Amount = 18m });

        _shifts.RecordCashMovement(new CashMovement
        {
            ShiftId = shift.Id, StaffId = staff.Id, Amount = -30m, Reason = "Supplier paid out",
        });

        var totals = _shifts.GetTotals(shift);

        Assert.Equal(25m, totals.CashSales);
        Assert.Equal(40m, totals.CardSales);
        Assert.Equal(18m, totals.PrepaidSales);
        Assert.Equal(-30m, totals.CashMovements);
        Assert.Equal(95m, totals.ExpectedCash);      // 100 + 25 - 30
        Assert.Equal(83m, totals.TotalTaken);        // card and prepaid are not in the drawer
        Assert.Equal(3, totals.OrdersPaid);
    }

    [Fact]
    public void Unsettled_orders_count_as_outstanding_not_as_takings()
    {
        var staff = AddStaff();
        var shift = _shifts.Open(staff.Id, 0m);
        Sale(shift, staff, 22m, new OrderTender { Type = TenderType.Cash, Amount = 7m });

        var totals = _shifts.GetTotals(shift);

        Assert.Equal(7m, totals.CashSales);
        Assert.Equal(15m, totals.OutstandingDue);
        Assert.Equal(1, totals.OrdersOpen);
        Assert.Equal(0, totals.OrdersPaid);
    }

    [Fact]
    public void Closing_records_the_variance_between_counted_and_expected()
    {
        var staff = AddStaff("Ann", StaffRole.Supervisor);
        var shift = _shifts.Open(staff.Id, 50m);
        Sale(shift, staff, 10m, new OrderTender { Type = TenderType.Cash, Amount = 10m });

        var totals = _shifts.GetTotals(shift);
        _shifts.Close(shift, staff.Id, declaredCash: 58m, expectedCash: totals.ExpectedCash, notes: null);

        var reloaded = _shifts.GetById(shift.Id)!;
        Assert.Equal(ShiftStatus.Closed, reloaded.Status);
        Assert.Equal(-2m, reloaded.Variance);
    }

    [Fact]
    public void Shift_numbers_are_sequential_and_never_reused()
    {
        var staff = AddStaff();
        var first = _shifts.Open(staff.Id, 0m);
        _shifts.Close(first, staff.Id, 0m, 0m, null);
        var second = _shifts.Open(staff.Id, 0m);

        Assert.Equal(first.Number + 1, second.Number);
    }

    [Fact]
    public void Permissions_stop_a_cashier_short_of_a_supervisor()
    {
        Assert.True(Permissions.Allows(StaffRole.Cashier, Permission.TakeOrders));
        Assert.False(Permissions.Allows(StaffRole.Cashier, Permission.Refund));
        Assert.True(Permissions.Allows(StaffRole.Supervisor, Permission.Refund));
        Assert.False(Permissions.Allows(StaffRole.Supervisor, Permission.EditSettings));
        Assert.True(Permissions.Allows(StaffRole.Manager, Permission.EditSettings));
    }

    [Fact]
    public void A_wrong_pin_authenticates_nobody()
    {
        AddStaff();
        Assert.NotNull(_staff.Authenticate("4321"));
        Assert.Null(_staff.Authenticate("0000"));
        Assert.Null(_staff.Authenticate(""));
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
