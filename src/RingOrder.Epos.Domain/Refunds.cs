namespace RingOrder.Epos.Domain;

/// <summary>One line handed back, when a refund names what went wrong.</summary>
public sealed class RefundLine
{
    /// <summary>The original order line, so the same dish is not refunded twice.</summary>
    public string LineId { get; set; } = "";

    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal Amount { get; set; }

    /// <summary>Band the money came off, so the VAT reversal is exact.</summary>
    public string? TaxClassId { get; set; }
}

/// <summary>
/// Money given back.
/// <para>
/// A refund is its own record, never an edit of the sale. The original order
/// keeps its lines, its totals and its VAT exactly as they were rung up, because
/// the shop has to be able to show both halves: what was sold, and what was
/// returned. A till that quietly reduced yesterday's takings would be unable to
/// explain either number.
/// </para>
/// </summary>
public sealed class Refund
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderId { get; set; } = "";
    public string? ShiftId { get; set; }

    /// <summary>Who gave the money back. Refunds are the classic way a till leaks.</summary>
    public string? StaffId { get; set; }

    /// <summary>Always positive: how much went back to the customer.</summary>
    public decimal Amount { get; set; }

    /// <summary>How it went back — normally the way it came in.</summary>
    public TenderType Tender { get; set; } = TenderType.Cash;

    /// <summary>Required. An unexplained refund is an unexplained hole in the takings.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Empty when the refund was an amount rather than named items.</summary>
    public List<RefundLine> Lines { get; set; } = [];

    public bool IsFull { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

    public string Summary => Lines.Count > 0
        ? $"{Money.Format(Amount)} — {string.Join(", ", Lines.Select(l => l.Name))}"
        : Money.Format(Amount);
}

/// <summary>
/// What may be refunded, and what must be refused.
/// <para>
/// Pure, so the rules that stop money leaving wrongly are tested without a
/// database or a screen behind them.
/// </para>
/// </summary>
public static class RefundPolicy
{
    /// <summary>
    /// What is still refundable: what was actually taken, less what has already
    /// gone back. Not the order total — an order half paid can only give half back.
    /// </summary>
    public static decimal Refundable(PosOrder order) =>
        Math.Max(0, Money.Round(order.AmountPaid - order.AmountRefunded));

    /// <summary>The tender a refund should default to: the one most of the money came in on.</summary>
    public static TenderType SuggestTender(PosOrder order) =>
        order.Tenders.Count == 0
            ? TenderType.Cash
            : order.Tenders
                .GroupBy(t => t.Type)
                .OrderByDescending(g => g.Sum(t => t.Amount))
                .First().Key;

    /// <summary>Null when the refund may go ahead, otherwise the plain reason it may not.</summary>
    public static string? Validate(PosOrder order, decimal amount, string? reason, bool zh = false)
    {
        string Say(string en, string cn) => zh ? cn : en;

        if (order.Status is PosOrderStatus.Voided)
            return Say("A voided order has nothing to refund.", "已作废的订单没有可退款项。");

        if (order.AmountPaid <= 0)
            return Say("Nothing has been paid on this order.", "该订单尚未收款。");

        if (amount <= 0)
            return Say("Enter an amount to refund.", "请输入退款金额。");

        var refundable = Refundable(order);
        if (refundable <= 0)
            return Say("This order has already been refunded in full.", "该订单已全额退款。");

        // The comparison carries a penny of tolerance so a full refund built by
        // summing lines is never refused by a rounding crumb.
        if (amount > refundable + 0.005m)
            return Say(
                $"Only {Money.Format(refundable)} is still refundable.",
                $"当前最多可退 {Money.Format(refundable)}。");

        if (string.IsNullOrWhiteSpace(reason))
            return Say("A refund needs a reason.", "退款必须填写原因。");

        return null;
    }

    /// <summary>
    /// Lines that may still be handed back — a dish already refunded does not
    /// appear twice, which is how a partial refund becomes a full one by
    /// accident.
    /// </summary>
    public static List<CartLine> RefundableLines(PosOrder order)
    {
        var alreadyDone = order.Refunds
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.LineId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        return order.Lines
            .Where(line => !alreadyDone.TryGetValue(line.Id, out var done) || done < line.Quantity)
            .ToList();
    }
}
