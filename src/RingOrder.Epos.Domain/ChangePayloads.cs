namespace RingOrder.Epos.Domain;

/// <summary>
/// What a change-log entry carries about an order.
/// <para>
/// A summary, not the whole aggregate. The order model is going to grow —
/// courses, seats, split bills — and an entry holding a serialised
/// <see cref="PosOrder"/> would either freeze that shape or fill the log with
/// versions of it. This holds the facts that answer "what happened", which is
/// what all three readers of the log actually want.
/// </para>
/// <para>
/// It may gain fields. That is safe for the same reason it is safe on the
/// entitlement token: readers ignore what they do not recognise, and an entry
/// verifies against the bytes that were written rather than against today's
/// shape.
/// </para>
/// <para>
/// Money is pence, as everywhere. A payload is hashed exactly as serialised, so
/// a decimal rendered differently by a future .NET would be a chain that stopped
/// verifying — integers cannot do that.
/// </para>
/// </summary>
public sealed record OrderSnapshot(
    string OrderNumber,
    string ServiceType,
    string Channel,
    string Status,
    int Lines,
    int TotalPence,
    int PaidPence,
    int BalancePence)
{
    public static OrderSnapshot Of(PosOrder order) => new(
        order.OrderNumber,
        order.ServiceType.ToString(),
        order.Channel.ToString(),
        order.Status.ToString(),
        order.Lines.Count,
        Money.ToPence(order.Total),
        Money.ToPence(order.AmountPaid),
        Money.ToPence(order.BalanceDue));
}

/// <summary>
/// One tender, as it was taken.
/// <para>
/// No card number, and there never can be one: the till is never given a PAN —
/// see <c>PaymentResult</c>. What is here is what a reconciliation needs, and
/// nothing a breach could spend.
/// </para>
/// </summary>
public sealed record PaymentSnapshot(
    string OrderNumber,
    string Tender,
    int AmountPence,
    string? Reference)
{
    public static PaymentSnapshot Of(PosOrder order, OrderTender tender) => new(
        order.OrderNumber,
        tender.Type.ToString(),
        Money.ToPence(tender.Amount),
        tender.Reference);
}

/// <summary>
/// A shift, as it was opened or closed.
/// <para>
/// The counted figure and the expected one are both here, and the variance is
/// deliberately not: it is their difference, and a stored difference is a third
/// number that can disagree with the two it came from.
/// </para>
/// </summary>
public sealed record ShiftSnapshot(
    int Number,
    string Status,
    int OpeningFloatPence,
    int? DeclaredCashPence,
    int? ExpectedCashPence)
{
    public static ShiftSnapshot Of(Shift shift) => new(
        shift.Number,
        shift.Status.ToString(),
        Money.ToPence(shift.OpeningFloat),
        shift.DeclaredCash is { } declared ? Money.ToPence(declared) : null,
        shift.ExpectedCash is { } expected ? Money.ToPence(expected) : null);
}

/// <summary>Which verb an order's save deserves, worked out rather than declared.</summary>
public static class OrderChangeVerb
{
    /// <summary>
    /// Derived from what changed, not passed in by the caller.
    /// <para>
    /// Deliberate: a log the callers have to remember to write is a log with
    /// holes in it, and the hole is always the path somebody added in a hurry.
    /// There is one write path for an order, so deriving it here means it cannot
    /// be forgotten.
    /// </para>
    /// </summary>
    /// <param name="before">The status on disk, or null if this order is new.</param>
    /// <param name="wasFullyPaid">Whether it was already settled before this save.</param>
    public static string For(PosOrderStatus? before, bool wasFullyPaid, PosOrder after)
    {
        if (before is null) return ChangeOp.Placed;

        // A void and a refund are different events and the shop's day shows
        // both — a sale that never happened, and one that did and was reversed.
        if (after.Status != before && after.Status is PosOrderStatus.Voided or PosOrderStatus.Cancelled)
            return ChangeOp.Voided;

        if (after.Status != before && after.Status == PosOrderStatus.Refunded)
            return ChangeOp.Refunded;

        // Settling is the event worth naming, so a reading of the log shows when
        // the money arrived rather than a run of indistinguishable amendments.
        if (!wasFullyPaid && after.IsFullyPaid) return ChangeOp.Paid;

        return ChangeOp.Amended;
    }
}
