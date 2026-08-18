namespace RingOrder.Epos.Domain;

/// <summary>
/// Which product a shop bought.
/// <para>
/// One signed binary is installed everywhere, and a word in the bundle is the
/// entire difference — the same rule that governs menus and printers. Two
/// installers would be a second build artefact, and the second copy of anything
/// is the one nobody keeps in step.
/// </para>
/// <para>
/// Deliberately a string rather than an enum: it arrives from a merchant's JSON
/// file, and an unknown word must fall back to something safe rather than throw
/// on a shop's first start. It will later be carried in the signed licence,
/// where a merchant cannot edit it — see DEPLOYMENT.md.
/// </para>
/// </summary>
public static class ShopEdition
{
    /// <summary>The full till: ordering, payment, shifts, reports.</summary>
    public const string Pos = "pos";

    /// <summary>
    /// Receives web orders and prints them. No till, no shifts, no forced
    /// sign-in. Lives in the tray because it sits in a corner unattended.
    /// </summary>
    public const string Print = "print";

    /// <summary>
    /// An unrecognised word means the full till.
    /// <para>
    /// Falling the safe way on purpose: a typo that silently downgraded a
    /// paying shop to a printer would take their till away mid-service, while a
    /// typo that gives a print-only machine a Till tab it never opens costs
    /// nobody a service.
    /// </para>
    /// </summary>
    public static string Normalise(string? raw) =>
        string.Equals(raw?.Trim(), Print, StringComparison.OrdinalIgnoreCase) ? Print : Pos;

    public static bool IsPrintOnly(string? edition) => Normalise(edition) == Print;
}
