namespace RingOrder.Epos.Domain;

public sealed class SelectedChoice
{
    public string ChoiceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string? OptionTranslation { get; set; }
    public decimal PriceDelta { get; set; }
}

public sealed class CartLineSelection
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public List<SelectedChoice> Choices { get; set; } = [];
}

public sealed class CartLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ItemId { get; set; }
    public string Name { get; set; } = "";
    public string? ItemTranslation { get; set; }
    public decimal BasePrice { get; set; }
    public int Quantity { get; set; } = 1;
    public List<CartLineSelection> Selections { get; set; } = [];
    public decimal LineTotal { get; set; }
    public string? Notes { get; set; }
    public bool IsAdHoc { get; set; }

    /// <summary>
    /// Station this line prints to, resolved when it was added. Held on the line
    /// rather than looked up later so re-routing the menu never changes what an
    /// old ticket said.
    /// </summary>
    public string? PrintClass { get; set; }

    /// <summary>VAT band at the time of sale, for the same reason.</summary>
    public string? TaxClassId { get; set; }
    /// <summary>True after this line has been printed to kitchen.</summary>
    public bool KitchenSent { get; set; }
    public DateTimeOffset? KitchenSentAt { get; set; }

    public string SentBadge => KitchenSent ? "SENT" : "";
    public bool HasExtras => Selections.Any(s => s.Choices.Count > 0) || !string.IsNullOrWhiteSpace(Notes);
    public bool HasTranslation => !string.IsNullOrWhiteSpace(ItemTranslation);

    public string DisplayLabel
    {
        get
        {
            var bits = new List<string> { Name };
            foreach (var sel in Selections)
            {
                foreach (var c in sel.Choices)
                    bits.Add($"+ {c.Label}");
            }
            if (!string.IsNullOrWhiteSpace(Notes))
                bits.Add($"NOTE: {Notes}");
            return string.Join(" / ", bits);
        }
    }

    public string OptionsSummary
    {
        get
        {
            var bits = new List<string>();
            foreach (var sel in Selections)
            {
                foreach (var c in sel.Choices)
                    bits.Add($"+ {c.Label}");
            }
            if (!string.IsNullOrWhiteSpace(Notes))
                bits.Add($"※ {Notes}");
            return string.Join("  ", bits);
        }
    }
}

public sealed class OrderTender
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TenderType Type { get; set; }
    /// <summary>Amount applied to the bill (never includes cash change).</summary>
    public decimal Amount { get; set; }
    /// <summary>Cash handed over by customer (cash tenders only).</summary>
    public decimal? CashReceived { get; set; }
    /// <summary>Change returned (cash tenders only).</summary>
    public decimal? ChangeGiven { get; set; }
    public string? Reference { get; set; }

    /// <summary>Who took the money — every payment is attributable.</summary>
    public string? StaffId { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

public sealed class PosOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderNumber { get; set; } = "";

    /// <summary>How the customer gets the food.</summary>
    public ServiceType ServiceType { get; set; } = ServiceType.Collection;

    /// <summary>Where the order came from.</summary>
    public OrderChannel Channel { get; set; } = OrderChannel.Counter;

    /// <summary>Marketplace name when <see cref="Channel"/> is Platform.</summary>
    public string? PlatformName { get; set; }

    /// <summary>
    /// Customer is standing at the counter waiting for this one. Prints WAITING
    /// on the kitchen ticket so it jumps the queue — it is a property of the
    /// moment, not a fourth kind of order.
    /// </summary>
    public bool CustomerWaiting { get; set; }

    public PosOrderStatus Status { get; set; } = PosOrderStatus.Draft;

    /// <summary>Till that took it — reserved for a second terminal.</summary>
    public string? TerminalId { get; set; }

    /// <summary>Staff member who opened the ticket.</summary>
    public string? StaffId { get; set; }

    /// <summary>Trading session this order belongs to, for X/Z reporting.</summary>
    public string? ShiftId { get; set; }

    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? DeliveryPostcode { get; set; }
    /// <summary>Table / pager number for Eat-in.</summary>
    public string? TableNumber { get; set; }
    /// <summary>Hold ticket label (name or phone).</summary>
    public string? HoldLabel { get; set; }
    public string? VoidReason { get; set; }
    public List<CartLine> Lines { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal DiscountTotal { get; set; }

    /// <summary>Why money came off. Required whenever DiscountTotal is not zero.</summary>
    public string? DiscountReason { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    /// <summary>Kitchen "Requested for:" (from website scheduled / ASAP).</summary>
    public string? RequestedFor { get; set; }
    public string? FulfilmentLabel { get; set; }
    /// <summary>CARD / CASH / … for kitchen ticket.</summary>
    public string? PaymentLabel { get; set; }
    public decimal BelowMinimumSurcharge { get; set; }
    public string? TicketFooter { get; set; }
    public string? OnlineExternalId { get; set; }
    public string? OnlinePayload { get; set; }
    public bool KitchenPrinted { get; set; }
    public bool FrontPrinted { get; set; }
    public bool OnlineAcked { get; set; }
    public List<OrderTender> Tenders { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Sum of tender amounts applied to the bill.</summary>
    public decimal AmountPaid => Tenders.Sum(t => t.Amount);

    /// <summary>Remaining to collect. Grows again if items are added after a partial/full pay reopen.</summary>
    public decimal BalanceDue => Math.Max(0, Math.Round(Total - AmountPaid, 2));

    public bool IsFullyPaid => BalanceDue <= 0.001m;
    public bool HasPayments => Tenders.Count > 0;

    /// <summary>Open for work and still owes money (excludes paid-in-full and voided).</summary>
    public bool IsUnpaid
    {
        get
        {
            var open = Status is PosOrderStatus.Draft or PosOrderStatus.Open
                or PosOrderStatus.Sent or PosOrderStatus.Held;
            return open && !IsFullyPaid;
        }
    }
    public int UnsentLineCount => Lines.Count(l => !l.KitchenSent);
    public bool HasUnsentLines => UnsentLineCount > 0;
}

/// <summary>
/// A person the shop takes orders from. Personal data in the plainest sense —
/// the ICO's own example of it is a name with an address — so everything here is
/// held under the retention rules in <c>docs/PRIVACY.md</c> and can be erased on
/// request without taking the shop's accounts with it.
/// </summary>
public sealed class Customer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Notes { get; set; }

    /// <summary>Links to places, loaded from <c>customer_addresses</c>.</summary>
    public List<CustomerAddress> Addresses { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Last time this customer ordered. Retention counts from here, not from
    /// when the record was created — a regular of ten years is not stale.
    /// </summary>
    public DateTimeOffset? LastOrderAt { get; set; }

    public CustomerAddress? DefaultAddress =>
        Addresses.FirstOrDefault(a => a.IsDefault) ?? Addresses.FirstOrDefault();
}
