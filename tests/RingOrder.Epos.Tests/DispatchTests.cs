using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The dispatch board. The question it exists to answer is the one a manager
/// asks at eleven at night: the drawer is £60 down, who is still out?
/// </summary>
public class DispatchTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ringorder-test-{Guid.NewGuid():N}.sqlite");
    private readonly EposDb _db;
    private readonly OrderRepository _orders;

    public DispatchTests()
    {
        _db = new EposDb(_dbPath);
        _db.Migrate();
        _orders = new OrderRepository(_db);
    }

    private static PosOrder Delivery(decimal total, decimal paid = 0, ServiceType service = ServiceType.Delivery)
    {
        var order = new PosOrder
        {
            OrderNumber = Guid.NewGuid().ToString("N")[..8],
            ServiceType = service,
            Channel = OrderChannel.Phone,
            Status = PosOrderStatus.Sent,
            DeliveryAddress = "12 Bristol Rd",
            KitchenPrinted = true,
            Lines = [new CartLine { Name = "Dish", BasePrice = total, Quantity = 1, IsAdHoc = true }],
            Total = total,
        };
        if (paid > 0)
        {
            order.Tenders.Add(new OrderTender { Type = TenderType.Cash, Amount = paid });
            if (paid >= total) order.Status = PosOrderStatus.Paid;
        }
        return order;
    }

    [Fact]
    public void Only_deliveries_reach_the_board()
    {
        var board = DispatchBoard.Build(
        [
            Delivery(20m),
            Delivery(15m, service: ServiceType.Collection),
            Delivery(30m, service: ServiceType.EatIn),
        ]);

        Assert.Single(board);
    }

    [Fact]
    public void A_voided_delivery_is_not_work()
    {
        var voided = Delivery(20m);
        voided.Status = PosOrderStatus.Voided;

        Assert.Empty(DispatchBoard.Build([voided]));
    }

    /// <summary>
    /// The distinction that keeps the driver totals honest. A web or marketplace
    /// order was paid at checkout, so the driver carries food and not money —
    /// counting it would have every driver appear to owe the shop the price of
    /// their whole round.
    /// </summary>
    [Fact]
    public void A_prepaid_delivery_puts_no_cash_in_the_drivers_pocket()
    {
        var prepaid = Delivery(24m, paid: 24m);
        var cashOnDelivery = Delivery(18m);

        Assert.Equal(0m, DispatchBoard.CashToCollect(prepaid));
        Assert.Equal(18m, DispatchBoard.CashToCollect(cashOnDelivery));
    }

    [Fact]
    public void A_part_paid_delivery_carries_only_the_balance()
    {
        Assert.Equal(15m, DispatchBoard.CashToCollect(Delivery(20m, paid: 5m)));
    }

    [Fact]
    public void Stages_follow_the_two_timestamps()
    {
        var order = Delivery(20m);
        Assert.Equal(DeliveryStage.Waiting, DispatchBoard.StageOf(order));

        order.DispatchedAt = DateTimeOffset.Now;
        Assert.Equal(DeliveryStage.WithDriver, DispatchBoard.StageOf(order));

        order.DeliveredAt = DateTimeOffset.Now;
        Assert.Equal(DeliveryStage.Delivered, DispatchBoard.StageOf(order));
    }

    /// <summary>
    /// The figure the drawer cannot account for on its own, and the reason any
    /// of this exists.
    /// </summary>
    [Fact]
    public void Cash_out_with_drivers_counts_only_what_is_actually_on_the_road()
    {
        var onTheRoad = Delivery(18m);
        onTheRoad.DispatchedAt = DateTimeOffset.Now;
        onTheRoad.DriverStaffId = "wei";

        var prepaidOnTheRoad = Delivery(24m, paid: 24m);
        prepaidOnTheRoad.DispatchedAt = DateTimeOffset.Now;
        prepaidOnTheRoad.DriverStaffId = "wei";

        var stillInTheShop = Delivery(30m);

        var alreadyBack = Delivery(12m);
        alreadyBack.DispatchedAt = DateTimeOffset.Now;
        alreadyBack.DeliveredAt = DateTimeOffset.Now;
        alreadyBack.DriverStaffId = "wei";

        var orders = new[] { onTheRoad, prepaidOnTheRoad, stillInTheShop, alreadyBack };

        Assert.Equal(18m, DispatchBoard.CashOutWithDrivers(orders));
    }

    [Fact]
    public void Each_driver_is_totalled_separately()
    {
        var first = Delivery(18m);
        first.DispatchedAt = DateTimeOffset.Now;
        first.DriverStaffId = "wei";

        var second = Delivery(22m);
        second.DispatchedAt = DateTimeOffset.Now;
        second.DriverStaffId = "wei";

        var other = Delivery(9m);
        other.DispatchedAt = DateTimeOffset.Now;
        other.DriverStaffId = "ann";

        var loads = DispatchBoard.Loads([first, second, other], id => id == "wei" ? "Wei" : "Ann");

        Assert.Equal(2, loads.Count);

        // Heaviest first: the person holding most of the shop's money is the one
        // a manager wants at the top of the list.
        Assert.Equal("Wei", loads[0].Name);
        Assert.Equal(40m, loads[0].CashHeld);
        Assert.Equal(2, loads[0].Orders);
        Assert.Equal(9m, loads[1].CashHeld);
    }

    [Fact]
    public void Sending_and_returning_survive_a_reload()
    {
        var order = Delivery(18m);
        _orders.Upsert(order);

        order.DriverStaffId = "wei";
        order.DispatchedAt = DateTimeOffset.Now;
        _orders.Upsert(order);

        var reloaded = _orders.GetById(order.Id)!;
        Assert.Equal("wei", reloaded.DriverStaffId);
        Assert.NotNull(reloaded.DispatchedAt);
        Assert.Null(reloaded.DeliveredAt);
        Assert.Equal(DeliveryStage.WithDriver, DispatchBoard.StageOf(reloaded));

        reloaded.DeliveredAt = DateTimeOffset.Now;
        _orders.Upsert(reloaded);

        Assert.Equal(DeliveryStage.Delivered, DispatchBoard.StageOf(_orders.GetById(order.Id)!));
    }

    /// <summary>
    /// A shop whose deliveries all go through Uber Eats has no drivers, and
    /// everything here reports nothing rather than something empty and puzzling.
    /// </summary>
    [Fact]
    public void A_shop_that_never_sends_a_driver_sees_nothing()
    {
        var orders = new[] { Delivery(20m), Delivery(15m, paid: 15m) };

        Assert.Empty(DispatchBoard.Loads(orders, _ => "—"));
        Assert.Equal(0m, DispatchBoard.CashOutWithDrivers(orders));
    }

    /// <summary>
    /// It reports rather than refuses, for the same reason a delivery minimum
    /// warns rather than blocks: a rule staff have to work around loses the
    /// record along with the sale.
    /// </summary>
    [Fact]
    public void Concerns_are_reported_and_never_enforced()
    {
        var notCooked = Delivery(20m);
        notCooked.KitchenPrinted = false;
        Assert.Contains("kitchen", DispatchBoard.ConcernAboutSending(notCooked));

        var noAddress = Delivery(20m);
        noAddress.DeliveryAddress = null;
        Assert.Contains("address", DispatchBoard.ConcernAboutSending(noAddress));

        Assert.Null(DispatchBoard.ConcernAboutSending(Delivery(20m)));

        // And it is still dispatchable — the person holding the bag decides.
        Assert.True(DispatchBoard.IsDispatchable(notCooked));
    }

    [Fact]
    public void A_driver_is_not_handed_the_till()
    {
        Assert.False(Permissions.Allows(StaffRole.Driver, Permission.TakeOrders));
        Assert.False(Permissions.Allows(StaffRole.Driver, Permission.Refund));
        Assert.False(Permissions.Allows(StaffRole.Driver, Permission.OpenDrawerWithoutSale));
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
