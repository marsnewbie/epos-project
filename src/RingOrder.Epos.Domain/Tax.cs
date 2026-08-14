namespace RingOrder.Epos.Domain;

/// <summary>What one VAT band contributed to an order.</summary>
public sealed record TaxBand(TaxClass Class, decimal Net, decimal Vat, decimal Gross)
{
    /// <summary>"20%" — how a receipt names the rate.</summary>
    public string RateLabel => $"{Class.RateBasisPoints / 100m:0.##}%";
}

/// <summary>
/// VAT on an order.
/// <para>
/// UK retail prices include VAT, so the arithmetic runs backwards from the
/// gross: a £6.00 dish at 20% is £5.00 net and £1.00 VAT, not £6.00 plus £1.20.
/// Getting this the wrong way round overstates a shop's takings by a fifth.
/// </para>
/// <para>
/// A shop below the registration threshold is not registered, and most small
/// takeaways are not. Nothing here is printed unless the shop has entered a VAT
/// number: a receipt claiming VAT from a business that cannot charge it is
/// worse than one that says nothing.
/// </para>
/// </summary>
public static class TaxCalculator
{
    /// <summary>
    /// Splits an order across its VAT bands.
    /// <para>
    /// An order-level discount is apportioned across the lines in proportion to
    /// their value before VAT is worked out, because a discount reduces the
    /// takings in every band it touches — not just the standard-rated one.
    /// </para>
    /// </summary>
    public static List<TaxBand> Summarise(
        PosOrder order,
        IReadOnlyList<TaxClass> classes,
        string defaultClassId = "hot-food",
        bool pricesIncludeTax = true)
    {
        if (classes.Count == 0) return [];

        var byId = classes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var fallback = byId.TryGetValue(defaultClassId, out var d) ? d : classes[0];

        var goods = Money.Round(order.Lines.Sum(l => l.LineTotal));
        var discount = Math.Min(order.DiscountTotal, goods);

        var grossByClass = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var line in order.Lines)
        {
            var taxClass = line.TaxClassId is { } id && byId.TryGetValue(id, out var found) ? found : fallback;

            // Proportional share of the discount. Guarded against a zero-value
            // ticket, which happens with free items.
            var share = goods > 0 ? line.LineTotal / goods : 0m;
            var gross = line.LineTotal - Money.Round(discount * share);

            grossByClass[taxClass.Id] = grossByClass.GetValueOrDefault(taxClass.Id) + gross;
        }

        // Delivery is ancillary to the food and follows the shop's default band.
        var extras = order.DeliveryFee + order.BelowMinimumSurcharge;
        if (extras > 0)
            grossByClass[fallback.Id] = grossByClass.GetValueOrDefault(fallback.Id) + extras;

        var bands = new List<TaxBand>();
        foreach (var (classId, rawGross) in grossByClass)
        {
            var taxClass = byId[classId];
            var gross = Money.Round(rawGross);
            if (gross == 0) continue;

            var vat = pricesIncludeTax
                ? Money.Round(gross * taxClass.Rate / (1 + taxClass.Rate))
                : Money.Round(gross * taxClass.Rate);

            var net = pricesIncludeTax ? Money.Round(gross - vat) : gross;
            bands.Add(new TaxBand(taxClass, net, vat, pricesIncludeTax ? gross : Money.Round(gross + vat)));
        }

        return bands.OrderByDescending(b => b.Class.RateBasisPoints).ToList();
    }

    public static decimal TotalVat(IEnumerable<TaxBand> bands) =>
        Money.Round(bands.Sum(b => b.Vat));
}
