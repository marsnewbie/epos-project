using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>
/// Sending deliveries out and taking the money back.
/// <para>
/// The arithmetic is in <see cref="DispatchBoard"/> and is pure. This is the
/// part that writes: which driver has a ticket, when it left, and the cash
/// tender created when they hand the money over.
/// </para>
/// </summary>
public sealed class DispatchService
{
    private readonly AppServices _app;

    public DispatchService(AppServices app) => _app = app;

    /// <summary>
    /// People who can be sent out. Drivers, plus anyone graded higher who also
    /// drives — a small shop's manager takes the van when it is busy, and a
    /// list that refused to show them would be a list nobody could use.
    /// </summary>
    public List<StaffMember> AvailableDrivers() =>
        _app.Staff.ListAll(activeOnly: true)
            .Where(s => s.Role != StaffRole.Cashier)
            .OrderBy(s => s.Role == StaffRole.Driver ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether this shop dispatches its own deliveries at all.
    /// <para>
    /// Derived from whether anyone is graded as a driver rather than from a
    /// setting, so a merchant whose deliveries all go through Uber Eats never
    /// sees a screen about drivers, and one who hires a driver gets it by adding
    /// them in Settings. Nothing configured behaves exactly as before.
    /// </para>
    /// </summary>
    public bool ShopUsesOwnDrivers() =>
        _app.Staff.ListAll(activeOnly: true).Any(s => s.Role == StaffRole.Driver);

    public List<PosOrder> TodaysDeliveries() =>
        _app.Orders.GetToday().Where(DispatchBoard.IsDispatchable).ToList();

    /// <summary>
    /// Hands a ticket to a driver and marks it gone.
    /// <para>
    /// One action rather than assign-then-send, because that is one action in
    /// the shop: the tickets go in a hand and the hand goes out of the door.
    /// </para>
    /// </summary>
    public void SendOut(IEnumerable<PosOrder> orders, StaffMember driver)
    {
        var now = DateTimeOffset.Now;

        foreach (var order in orders)
        {
            order.DriverStaffId = driver.Id;
            order.DispatchedAt = now;
            order.DeliveredAt = null;
            order.UpdatedAt = now;
            _app.Orders.Upsert(order);

            _app.Session.Record("delivery.out", order.Id,
                $"{order.OrderNumber} with {driver.Name}" +
                (DispatchBoard.CashToCollect(order) > 0
                    ? $" — £{DispatchBoard.CashToCollect(order):0.00} to collect"
                    : " — already paid"));
        }
    }

    /// <summary>
    /// A driver is back. Anything still owing is taken as cash into the drawer,
    /// and the delivery is closed.
    /// <para>
    /// The tender is stamped with whoever is signed in — the person who received
    /// the money — while the order keeps the driver who collected it. Both names
    /// matter, and they are usually different people: "the drawer is short"
    /// needs to distinguish the driver who was given it from the person who put
    /// it in.
    /// </para>
    /// </summary>
    public decimal Settle(IEnumerable<PosOrder> orders)
    {
        var now = DateTimeOffset.Now;
        var collected = 0m;

        foreach (var order in orders)
        {
            var owed = DispatchBoard.CashToCollect(order);
            if (owed > 0)
            {
                var tender = new OrderTender { Type = TenderType.Cash, Amount = owed };
                _app.Session.Stamp(tender);
                order.Tenders.Add(tender);
                order.Status = PosOrderStatus.Paid;
                collected += owed;
            }

            order.DeliveredAt = now;
            order.UpdatedAt = now;
            _app.Orders.Upsert(order);

            _app.Session.Record("delivery.back", order.Id,
                owed > 0 ? $"{order.OrderNumber} — £{owed:0.00} cash in" : $"{order.OrderNumber} — nothing owed");
        }

        return Money.Round(collected);
    }

    /// <summary>
    /// Puts a delivery back on the counter — the driver could not find it, or it
    /// was handed to the wrong person.
    /// <para>
    /// Money already taken is not touched. Un-sending a delivery is a change of
    /// where the food is, not a refund, and the two must not be confused.
    /// </para>
    /// </summary>
    public void Recall(PosOrder order)
    {
        order.DriverStaffId = null;
        order.DispatchedAt = null;
        order.DeliveredAt = null;
        order.UpdatedAt = DateTimeOffset.Now;
        _app.Orders.Upsert(order);
        _app.Session.Record("delivery.recalled", order.Id, order.OrderNumber);
    }
}
