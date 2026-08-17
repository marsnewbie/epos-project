namespace RingOrder.Epos.Domain;

/// <summary>Where a delivery has got to.</summary>
public enum DeliveryStage
{
    /// <summary>Made, or being made, and still in the shop.</summary>
    Waiting,

    /// <summary>Out on the road with a driver.</summary>
    WithDriver,

    /// <summary>Delivered, and any money owed has come back.</summary>
    Delivered,
}

/// <summary>One delivery on the board.</summary>
public sealed record DispatchEntry(PosOrder Order, DeliveryStage Stage, decimal CashToCollect)
{
    public bool CarriesCash => CashToCollect > 0;
}

/// <summary>What one driver is holding right now.</summary>
public sealed record DriverLoad(string StaffId, string Name, int Orders, decimal CashHeld);

/// <summary>
/// The dispatch board: which deliveries are in the shop, which are on the road,
/// and how much of the shop's money is in each driver's pocket.
/// <para>
/// Pure, like the rest of the arithmetic in this domain, so the question a
/// manager asks at eleven at night — "the drawer is £60 down, who is still
/// out?" — is a test rather than a reconstruction.
/// </para>
/// <para>
/// A shop whose deliveries are all done by Uber Eats or Deliveroo has no
/// drivers, and everything here reports nothing. That is deliberate: the two
/// kinds of merchant run the same binary, and the one that never sees a driver
/// must never see a driver screen.
/// </para>
/// </summary>
public static class DispatchBoard
{
    /// <summary>
    /// Deliveries worth showing. Collection and eat-in are not deliveries, and
    /// a voided order is not work.
    /// </summary>
    public static bool IsDispatchable(PosOrder order) =>
        order.ServiceType == ServiceType.Delivery &&
        order.Status is not (PosOrderStatus.Voided or PosOrderStatus.Cancelled);

    public static DeliveryStage StageOf(PosOrder order) =>
        order.DeliveredAt is not null ? DeliveryStage.Delivered
        : order.DispatchedAt is not null ? DeliveryStage.WithDriver
        : DeliveryStage.Waiting;

    /// <summary>
    /// What the driver has to bring back for this order.
    /// <para>
    /// Zero for anything already settled, which is most web and every
    /// marketplace order — the customer paid at checkout and the driver carries
    /// food, not money. Treating those as cash on delivery would have every
    /// driver appear to owe the shop the price of their whole round.
    /// </para>
    /// </summary>
    public static decimal CashToCollect(PosOrder order) =>
        order.IsFullyPaid ? 0m : order.BalanceDue;

    public static List<DispatchEntry> Build(IEnumerable<PosOrder> orders) =>
        orders
            .Where(IsDispatchable)
            .Select(o => new DispatchEntry(o, StageOf(o), CashToCollect(o)))
            .OrderBy(e => e.Stage)
            .ThenBy(e => e.Order.CreatedAt)
            .ToList();

    /// <summary>
    /// Money the shop is owed by its own drivers, right now.
    /// <para>
    /// This is the figure the drawer cannot explain on its own. A shift that
    /// looks £60 short at eleven o'clock is usually a driver who has not come
    /// back yet, and a till that cannot say so sends someone looking for a
    /// thief.
    /// </para>
    /// </summary>
    public static decimal CashOutWithDrivers(IEnumerable<PosOrder> orders) =>
        Money.Round(orders
            .Where(o => IsDispatchable(o) && StageOf(o) == DeliveryStage.WithDriver)
            .Sum(CashToCollect));

    /// <summary>Per driver, for the board and for the handover at the end of a run.</summary>
    public static List<DriverLoad> Loads(IEnumerable<PosOrder> orders, Func<string, string> nameOf) =>
        orders
            .Where(o => IsDispatchable(o) &&
                        StageOf(o) == DeliveryStage.WithDriver &&
                        !string.IsNullOrWhiteSpace(o.DriverStaffId))
            .GroupBy(o => o.DriverStaffId!)
            .Select(g => new DriverLoad(
                g.Key,
                nameOf(g.Key),
                g.Count(),
                Money.Round(g.Sum(CashToCollect))))
            .OrderByDescending(d => d.CashHeld)
            .ToList();

    /// <summary>
    /// Why an order cannot go out yet, or null when it can.
    /// <para>
    /// It reports rather than refuses, for the same reason a delivery minimum
    /// warns rather than blocks: the person holding the bag can see things the
    /// till cannot, and a rule they have to work around loses the record along
    /// with the sale.
    /// </para>
    /// </summary>
    public static string? ConcernAboutSending(PosOrder order)
    {
        if (!order.KitchenPrinted) return "not sent to the kitchen yet";
        if (string.IsNullOrWhiteSpace(order.DeliveryAddress)) return "no address on the ticket";
        return null;
    }
}
