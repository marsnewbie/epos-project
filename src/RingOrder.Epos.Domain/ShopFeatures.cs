namespace RingOrder.Epos.Domain;

/// <summary>
/// The optional modules a shop can be granted, by name.
/// <para>
/// Constants rather than an enum: these words are written into a signed token by
/// a service in another language and read back here, so a renumbering must not
/// be possible.
/// </para>
///
/// <para><b>Only optional modules belong here, and only these are ever gated.</b></para>
/// <para>
/// Ringing a sale, taking money, closing a shift, the menu, staff, Settings —
/// none of that is ever checked against an entitlement. Two reasons, and the
/// second is the one that bites:
/// </para>
/// <list type="number">
/// <item>
/// A till that could hide its own Till tab is a till that can be bricked by a
/// bad row in a database three hundred miles away.
/// </item>
/// <item>
/// The feature list is an <b>allow-list</b>: naming one module denies every
/// other. So the moment anything core were gated, granting a shop "drivers"
/// would take away its ability to sell food.
/// </item>
/// </list>
/// <para>
/// An empty list restricts nothing, which is why turning entitlements on changed
/// nothing for any existing shop. To sell modules, list what the shop bought.
/// </para>
/// </summary>
public static class ShopFeatures
{
    /// <summary>
    /// The delivery board. Genuinely optional: plenty of merchants deliver
    /// entirely through Uber Eats and must never see a screen about drivers.
    /// </summary>
    public const string Drivers = "drivers";

    /// <summary>
    /// Caller ID. Optional because it needs a box on the phone line, and a shop
    /// without one gains nothing from the setting existing.
    /// </summary>
    public const string CallerId = "caller-id";

    /// <summary>
    /// Every name this build knows. Shown in Settings → Cloud so an operator can
    /// see what there is to grant, rather than having to remember the spelling.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Drivers, CallerId];
}
