using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>What a refund attempt did, or why it did nothing.</summary>
public sealed record RefundResult(bool Ok, string Message, Refund? Refund = null)
{
    public static RefundResult Refused(string why) => new(false, why);
}

/// <summary>
/// Gives money back, and leaves a record of it.
/// <para>
/// Refunds are the classic way a till leaks: they are the one action that moves
/// money outwards on a member of staff's say-so. So every one of them is gated
/// on <see cref="Permission.Refund"/>, carries a reason, names the person, and
/// belongs to a shift. None of that stops a determined thief — it makes an
/// honest shop able to see what happened, which is the achievable goal.
/// </para>
/// </summary>
public sealed class RefundService
{
    private readonly AppServices _app;

    public RefundService(AppServices app) => _app = app;

    /// <summary>
    /// Records a refund against a settled order and prints the customer's proof.
    /// <para>
    /// The order is never rewritten. Its lines, totals and VAT stay as they were
    /// rung up, and the refund sits beside them — the shop has to be able to show
    /// both the sale and the reversal, and a mutated original can show neither.
    /// </para>
    /// </summary>
    public async Task<RefundResult> RefundAsync(
        PosOrder order,
        decimal amount,
        string reason,
        TenderType tender,
        IReadOnlyList<CartLine>? lines = null,
        bool print = true)
    {
        var refusal = RefundPolicy.Validate(order, amount, reason, UiText.IsZh);
        if (refusal is not null) return RefundResult.Refused(refusal);

        var refund = new Refund
        {
            OrderId = order.Id,
            ShiftId = _app.Session.Shift?.Id,
            StaffId = _app.Session.Staff?.Id,
            Amount = Money.Round(amount),
            Tender = tender,
            Reason = reason.Trim(),
            IsFull = amount >= RefundPolicy.Refundable(order) - 0.005m,
            Lines = (lines ?? [])
                .Select(l => new RefundLine
                {
                    LineId = l.Id,
                    Name = l.Name,
                    Quantity = l.Quantity,
                    Amount = l.LineTotal,
                    TaxClassId = l.TaxClassId,
                })
                .ToList(),
        };

        _app.RefundRepo.Record(refund);
        order.Refunds.Add(refund);

        // Status changes only when everything taken has gone back. A partial
        // refund leaves a paid order paid — it is still a sale, just a smaller
        // one, and marking it otherwise would hide it from the day's takings.
        if (order.IsFullyRefunded && order.Status is not PosOrderStatus.Voided)
        {
            order.Status = PosOrderStatus.Refunded;
            order.UpdatedAt = DateTimeOffset.Now;
            _app.Orders.Upsert(order);
        }

        _app.Session.Record("order.refund", order.Id,
            $"{order.OrderNumber} — {Money.Format(refund.Amount)} {refund.Tender} — {refund.Reason}");
        AppLog.Info("refund",
            $"{order.OrderNumber} {Money.Format(refund.Amount)} {refund.Tender}: {refund.Reason}");

        // Cash back means the drawer opens, for the same reason it opens on a
        // cash sale: somebody has to reach into it.
        if (refund.Tender == TenderType.Cash && _app.GetSettings().OpenDrawerOnCash)
        {
            try
            {
                await _app.Print.OpenDrawerAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn("refund", $"drawer did not open: {ex.Message}");
            }
        }

        if (print)
        {
            try
            {
                await _app.Print.PrintRefundAsync(order, refund);
            }
            catch (Exception ex)
            {
                // The money has already gone back and is recorded. A printer
                // fault must not make the refund look as though it failed.
                AppLog.Error("refund", "refund receipt did not print", ex);
                return new RefundResult(true,
                    UiText.Pick(
                        $"Refunded {Money.Format(refund.Amount)} — receipt did not print",
                        $"已退款 {Money.Format(refund.Amount)}——小票未打印"),
                    refund);
            }
        }

        return new RefundResult(true,
            UiText.Pick(
                $"Refunded {Money.Format(refund.Amount)} to {Describe(refund.Tender)}",
                $"已退款 {Money.Format(refund.Amount)}（{Describe(refund.Tender)}）"),
            refund);
    }

    private static string Describe(TenderType tender) => tender switch
    {
        TenderType.Cash => UiText.Pick("cash", "现金"),
        TenderType.CardManual or TenderType.CardIntegrated => UiText.Pick("card", "银行卡"),
        TenderType.PrepaidOnline => UiText.Pick("the original online payment", "线上原路"),
        TenderType.Voucher => UiText.Pick("voucher", "代金券"),
        _ => UiText.Pick("other", "其他"),
    };
}
