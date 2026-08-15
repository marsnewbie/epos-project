namespace RingOrder.Epos.Domain;

/// <summary>
/// One delivery area, priced by postcode prefix.
/// <para>
/// Prefixes rather than distance because that is how a takeaway actually
/// publishes its delivery area — the leaflet says "B44, B23, B42 — £2", not
/// "£1 per mile". Staff can check a prefix against a customer's postcode without
/// a map, it works with the broadband down, and it does not invite the argument
/// that starts "your screen says 3.1 miles but I'm 2.9".
/// </para>
/// </summary>
public sealed class DeliveryZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Matched against the postcode with spaces removed, so "B44" and "B440"
    /// are both usable — the second narrows to a sector when one part of a
    /// district costs more to reach.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>What the shop calls it on the phone, e.g. "Kingstanding".</summary>
    public string Name { get; set; } = "";

    public decimal Fee { get; set; }

    /// <summary>Food value the order must reach. Zero means no minimum.</summary>
    public decimal MinimumOrder { get; set; }

    /// <summary>Food value above which delivery is free. Zero means never.</summary>
    public decimal FreeOverAmount { get; set; }

    /// <summary>
    /// A prefix the shop will not deliver to. Worth stating rather than leaving
    /// out: "we don't go there" is a real answer, and a zone that says so stops
    /// a driver being sent while somebody looks for the missing entry.
    /// </summary>
    public bool IsDeliverable { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Normalised for matching: uppercase, letters and digits only.</summary>
    public string NormalisedPrefix => Normalise(Prefix);

    public static string Normalise(string? value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

/// <summary>What the shop does when an order is under a zone's minimum.</summary>
public enum BelowMinimumPolicy
{
    /// <summary>Say so and carry on. The person on the phone decides.</summary>
    Warn,

    /// <summary>Add the shortfall to the bill so the order reaches the minimum.</summary>
    Surcharge,
}

/// <summary>What the shop does with a postcode no zone covers.</summary>
public enum OutsideZonePolicy
{
    /// <summary>Charge the default fee, and say it matched nothing.</summary>
    ChargeDefault,

    /// <summary>Treat it as outside the delivery area, and say so loudly.</summary>
    Refuse,
}

/// <summary>
/// The delivery charge for one order, and everything staff need to be told
/// about it.
/// </summary>
public sealed record DeliveryQuote(
    decimal Fee,
    decimal Surcharge,
    DeliveryZone? Zone,
    bool OutsideArea,
    decimal Shortfall,
    string Message)
{
    /// <summary>True when staff should be looking at this before sending a driver.</summary>
    public bool NeedsAttention => OutsideArea || Shortfall > 0;

    public static readonly DeliveryQuote None =
        new(0, 0, null, false, 0, "");
}

/// <summary>
/// Works out what delivery costs. Pure: no database, no screen, no settings
/// object — the rules that decide what a customer is charged are testable on
/// their own.
/// </summary>
public static class DeliveryPricing
{
    /// <summary>
    /// Longest matching prefix wins.
    /// <para>
    /// "B44 0QN" takes a "B440" zone over a "B44" zone over a "B4" zone. The
    /// broad one is a deliberate fallback, not an accident: a shop that writes
    /// "B4" and has no "B44" entry means "that side of town", and charging the
    /// broader zone beats declaring a real customer unreachable. The matched zone
    /// is shown on screen so a shop can see it happen and add the narrower entry
    /// if that was not what they meant.
    /// </para>
    /// </summary>
    public static DeliveryZone? Match(IEnumerable<DeliveryZone> zones, UkPostcode postcode)
    {
        var packed = DeliveryZone.Normalise(postcode.Value);
        if (packed.Length == 0) return null;

        return zones
            .Where(z => z.NormalisedPrefix.Length > 0 && packed.StartsWith(z.NormalisedPrefix, StringComparison.Ordinal))
            .OrderByDescending(z => z.NormalisedPrefix.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Prices a delivery.
    /// <para>
    /// <paramref name="goodsValue"/> is the food, after any discount and before
    /// delivery — a minimum order is about how much food is worth carrying, and
    /// counting the delivery fee towards it would let the fee justify itself.
    /// </para>
    /// </summary>
    public static DeliveryQuote Quote(
        IReadOnlyList<DeliveryZone> zones,
        UkPostcode postcode,
        decimal goodsValue,
        decimal defaultFee,
        BelowMinimumPolicy belowMinimum = BelowMinimumPolicy.Warn,
        OutsideZonePolicy outside = OutsideZonePolicy.ChargeDefault,
        bool zh = false)
    {
        string Say(string en, string cn) => zh ? cn : en;

        if (postcode.IsEmpty)
            return new DeliveryQuote(defaultFee, 0, null, false, 0,
                Say("No postcode yet — default delivery fee.", "尚未填写邮编——按默认配送费。"));

        var zone = Match(zones, postcode);

        if (zone is null)
        {
            // No zones configured at all is not "outside the area" — it is a shop
            // that has not set any up, and it should behave as it did before.
            if (zones.Count == 0)
                return new DeliveryQuote(defaultFee, 0, null, false, 0, "");

            return outside == OutsideZonePolicy.Refuse
                ? new DeliveryQuote(defaultFee, 0, null, true, 0,
                    Say($"{postcode.Value} is outside the delivery area.",
                        $"{postcode.Value} 不在配送范围内。"))
                : new DeliveryQuote(defaultFee, 0, null, false, 0,
                    Say($"{postcode.Value} matches no zone — default fee applied.",
                        $"{postcode.Value} 未匹配任何区域——按默认配送费。"));
        }

        if (!zone.IsDeliverable)
            return new DeliveryQuote(0, 0, zone, true, 0,
                Say($"{Label(zone)}: the shop does not deliver here.",
                    $"{Label(zone)}：该区域不配送。"));

        var freeDelivery = zone.FreeOverAmount > 0 && goodsValue >= zone.FreeOverAmount;
        var fee = freeDelivery ? 0m : zone.Fee;

        var shortfall = zone.MinimumOrder > 0 && goodsValue < zone.MinimumOrder
            ? Money.Round(zone.MinimumOrder - goodsValue)
            : 0m;

        // A shortfall is never a hard stop. The owner on the phone may well take
        // the order anyway, and a till that refuses outright is a till staff work
        // around — which loses the record of what happened along with the sale.
        var surcharge = shortfall > 0 && belowMinimum == BelowMinimumPolicy.Surcharge
            ? shortfall
            : 0m;

        var message = shortfall > 0
            ? belowMinimum == BelowMinimumPolicy.Surcharge
                ? Say($"{Label(zone)}: under the {Money.Format(zone.MinimumOrder)} minimum — {Money.Format(surcharge)} added.",
                      $"{Label(zone)}：未达 {Money.Format(zone.MinimumOrder)} 起送——已加收 {Money.Format(surcharge)}。")
                : Say($"{Label(zone)}: under the {Money.Format(zone.MinimumOrder)} minimum by {Money.Format(shortfall)}.",
                      $"{Label(zone)}：距 {Money.Format(zone.MinimumOrder)} 起送还差 {Money.Format(shortfall)}。")
            : freeDelivery
                ? Say($"{Label(zone)}: free delivery over {Money.Format(zone.FreeOverAmount)}.",
                      $"{Label(zone)}：满 {Money.Format(zone.FreeOverAmount)} 免配送费。")
                : Say($"{Label(zone)}: {Money.Format(fee)} delivery.",
                      $"{Label(zone)}：配送费 {Money.Format(fee)}。");

        return new DeliveryQuote(fee, surcharge, zone, false, shortfall, message);
    }

    private static string Label(DeliveryZone zone) =>
        string.IsNullOrWhiteSpace(zone.Name) ? zone.Prefix : $"{zone.Prefix} {zone.Name}";
}
