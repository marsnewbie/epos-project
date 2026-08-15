namespace RingOrder.Epos.Domain;

/// <summary>How a shop prices distance.</summary>
public enum DeliveryMode
{
    /// <summary>Postcode rules only. No rule matches, no delivery.</summary>
    Postcode,

    /// <summary>Road-distance bands only.</summary>
    Miles,

    /// <summary>Try the postcode rules first, fall back to distance.</summary>
    Hybrid,
}

/// <summary>
/// One delivery area, priced by postcode.
/// <para>
/// <see cref="Prefix"/> is matched on structured postcode components, never as a
/// string — see <see cref="PostcodeRules"/> for why B47 must not match a B44
/// rule.
/// </para>
/// </summary>
public sealed class DeliveryZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"B", "B44", "B44 0" or "B44 0QN". The space is significant.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>What the shop calls it on the phone, e.g. "Kingstanding".</summary>
    public string Name { get; set; } = "";

    public decimal Fee { get; set; }

    /// <summary>Order value the basket must reach. Zero means no minimum.</summary>
    public decimal MinimumOrder { get; set; }

    /// <summary>
    /// Order value at or above which this zone delivers free. Zero means no
    /// threshold — never "free from the first penny", which is a delivery fee of
    /// zero. Giving one idea two spellings is how a merchant clears the box and
    /// accidentally makes every order free.
    /// </summary>
    public decimal FreeOverAmount { get; set; }

    /// <summary>Switched off rather than deleted, so a seasonal area keeps its prices.</summary>
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public string Canonical => PostcodeRules.Canonical(Prefix);
}

/// <summary>One road-distance band: <c>MinMiles &lt;= d &lt; MaxMiles</c>.</summary>
public sealed class MilesBand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public decimal MinMiles { get; set; }
    public decimal MaxMiles { get; set; }
    public decimal Fee { get; set; }
    public decimal MinimumOrder { get; set; }

    /// <summary>Zero means no threshold, as on a zone.</summary>
    public decimal FreeOverAmount { get; set; }

    public int SortOrder { get; set; }

    public string Label => $"{MinMiles:0.##}–{MaxMiles:0.##} miles";
}

/// <summary>Everything the pricing needs, so the rules stay free of settings plumbing.</summary>
public sealed class DeliveryConfig
{
    public DeliveryMode Mode { get; set; } = DeliveryMode.Postcode;

    /// <summary>Charged when the shop has configured nothing at all.</summary>
    public decimal DefaultFee { get; set; }

    /// <summary>
    /// Flat amount added when the basket is under the matched minimum — the
    /// shop's price for carrying a small order, not the shortfall. Zero means the
    /// till warns and charges nothing extra.
    /// </summary>
    public decimal BelowMinimumSurcharge { get; set; }

    public decimal MaxDeliveryMiles { get; set; } = 5m;

    public IReadOnlyList<DeliveryZone> Zones { get; set; } = [];
    public IReadOnlyList<MilesBand> MilesBands { get; set; } = [];
}

/// <summary>The delivery charge for one order, and what staff should be told.</summary>
public sealed record DeliveryQuote(
    bool Eligible,
    decimal Fee,
    decimal Surcharge,
    decimal MinimumOrder,
    bool MeetsMinimum,
    string MatchedRule,
    decimal? DistanceMiles,
    string Message)
{
    /// <summary>What goes on the bill for delivery, all in.</summary>
    public decimal TotalDeliveryCharge => Money.Round(Fee + Surcharge);

    /// <summary>True when staff should look at this before sending a driver.</summary>
    public bool NeedsAttention => !Eligible || !MeetsMinimum;

    public static readonly DeliveryQuote None =
        new(true, 0, 0, 0, true, "", null, "");
}

/// <summary>
/// Works out what delivery costs.
/// <para>
/// Pure, and deliberately a port of the website's <c>calculateDeliveryQuote</c>.
/// A shop running both must quote one price: if the site says "we don't deliver
/// to B47" while the till charges the B4 rate, the merchant finds out from a
/// customer.
/// </para>
/// </summary>
public static class DeliveryPricing
{
    /// <summary>
    /// The most specific matching rule wins: unit beats sector beats district
    /// beats area. Inactive zones are not considered.
    /// </summary>
    public static DeliveryZone? Match(IEnumerable<DeliveryZone> zones, UkPostcode postcode)
    {
        if (!postcode.IsValid) return null;

        DeliveryZone? best = null;
        var bestLevel = 0;

        foreach (var zone in zones)
        {
            if (!zone.IsActive) continue;

            var rule = PostcodeRules.Parse(zone.Prefix);
            if (rule is null || !PostcodeRules.Covers(rule, postcode)) continue;

            if ((int)rule.Level > bestLevel)
            {
                best = zone;
                bestLevel = (int)rule.Level;
            }
        }

        return best;
    }

    public static MilesBand? MatchBand(IEnumerable<MilesBand> bands, decimal miles) =>
        bands.FirstOrDefault(b => miles >= b.MinMiles && miles < b.MaxMiles);

    /// <summary>
    /// Prices a delivery.
    /// <para>
    /// <paramref name="orderValue"/> is the basket <i>before</i> discounts, which
    /// is what the customer actually ordered — a voucher must not quietly
    /// withdraw the free delivery they were already shown.
    /// </para>
    /// </summary>
    public static DeliveryQuote Quote(
        DeliveryConfig config,
        UkPostcode postcode,
        decimal orderValue,
        decimal? distanceMiles = null,
        bool zh = false)
    {
        string Say(string en, string cn) => zh ? cn : en;

        // A shop that has configured nothing behaves as it always did: one fee,
        // and nothing said. "Nothing set up" is not "outside the delivery area".
        if (config.Zones.Count == 0 && config.MilesBands.Count == 0)
            return new DeliveryQuote(true, config.DefaultFee, 0, 0, true, "", distanceMiles, "");

        if (postcode.IsEmpty)
            return new DeliveryQuote(true, config.DefaultFee, 0, 0, true, "", distanceMiles,
                Say("No postcode yet — default delivery fee.", "尚未填写邮编——按默认配送费。"));

        if (config.Mode is DeliveryMode.Postcode or DeliveryMode.Hybrid && postcode.IsValid)
        {
            var zone = Match(config.Zones, postcode);
            if (zone is not null)
                return Build(config, orderValue, zone.Fee, zone.MinimumOrder, zone.FreeOverAmount,
                    Label(zone), distanceMiles, zh);

            if (config.Mode == DeliveryMode.Postcode)
                return new DeliveryQuote(false, 0, 0, 0, true, "", distanceMiles,
                    Say($"{postcode.Value} is outside the delivery area.",
                        $"{postcode.Value} 不在配送范围内。"));
        }

        if (config.Mode is DeliveryMode.Miles or DeliveryMode.Hybrid && distanceMiles is { } miles)
        {
            if (miles > config.MaxDeliveryMiles)
                return new DeliveryQuote(false, 0, 0, 0, true, "", miles,
                    Say($"{miles:0.0} miles — beyond the {config.MaxDeliveryMiles:0.##} mile limit.",
                        $"{miles:0.0} 英里——超出 {config.MaxDeliveryMiles:0.##} 英里配送半径。"));

            var band = MatchBand(config.MilesBands, miles);
            if (band is not null)
                return Build(config, orderValue, band.Fee, band.MinimumOrder, band.FreeOverAmount,
                    band.Label, miles, zh);
        }

        // Distance was wanted and could not be had — no coordinates, or the
        // routing service was unreachable. Say so rather than inventing a price.
        return new DeliveryQuote(true, config.DefaultFee, 0, 0, true, "", distanceMiles,
            Say("Could not price this postcode — default fee applied, please check.",
                "无法计算该邮编的配送费——已按默认收费，请人工确认。"));
    }

    private static DeliveryQuote Build(
        DeliveryConfig config,
        decimal orderValue,
        decimal baseFee,
        decimal minimumOrder,
        decimal freeOver,
        string matchedRule,
        decimal? distanceMiles,
        bool zh)
    {
        string Say(string en, string cn) => zh ? cn : en;

        var meetsMinimum = orderValue >= minimumOrder;
        var surcharge = meetsMinimum ? 0m : config.BelowMinimumSurcharge;

        // Zero is "no threshold", never "free from the first penny".
        var freeDelivery = freeOver > 0 && orderValue >= freeOver;
        var fee = freeDelivery ? 0m : baseFee;

        var message = !meetsMinimum
            ? surcharge > 0
                ? Say($"{matchedRule}: under the {Money.Format(minimumOrder)} minimum — {Money.Format(surcharge)} surcharge.",
                      $"{matchedRule}：未达 {Money.Format(minimumOrder)} 起送——加收 {Money.Format(surcharge)}。")
                : Say($"{matchedRule}: under the {Money.Format(minimumOrder)} minimum.",
                      $"{matchedRule}：未达 {Money.Format(minimumOrder)} 起送。")
            : freeDelivery
                ? Say($"{matchedRule}: free delivery over {Money.Format(freeOver)}.",
                      $"{matchedRule}：满 {Money.Format(freeOver)} 免配送费。")
                : Say($"{matchedRule}: {Money.Format(fee)} delivery.",
                      $"{matchedRule}：配送费 {Money.Format(fee)}。");

        return new DeliveryQuote(
            true, fee, surcharge, minimumOrder, meetsMinimum, matchedRule, distanceMiles, message);
    }

    private static string Label(DeliveryZone zone) =>
        string.IsNullOrWhiteSpace(zone.Name) ? zone.Canonical : $"{zone.Canonical} {zone.Name}";
}
