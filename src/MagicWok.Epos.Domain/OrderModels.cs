namespace MagicWok.Epos.Domain;

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
                bits.Add($"NOTE: {Notes}");
            return string.Join("  ", bits);
        }
    }
}

public sealed class OrderTender
{
    public TenderType Type { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

public sealed class PosOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderNumber { get; set; } = "";
    public PosOrderType OrderType { get; set; } = PosOrderType.Collection;
    public PosOrderSource Source { get; set; } = PosOrderSource.Pos;
    public PosOrderStatus Status { get; set; } = PosOrderStatus.Draft;
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? DeliveryPostcode { get; set; }
    public List<CartLine> Lines { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal DiscountTotal { get; set; }
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
}

public sealed class Customer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Notes { get; set; }
    public List<CustomerAddress> Addresses { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CustomerAddress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Home";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string Postcode { get; set; } = "";
    public bool IsDefault { get; set; }
}

public sealed class PrintJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OrderId { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public PrintJobChannel Channel { get; set; }
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;
    public string? PayloadText { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? PrintedAt { get; set; }
}
