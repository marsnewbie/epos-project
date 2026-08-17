using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>
/// Gathers the rows an X or Z reading is worked out from.
/// <para>
/// The arithmetic itself lives in <see cref="ShiftReportBuilder"/> and is pure.
/// This is only the fetching: totals from the payments carrying the shift id,
/// orders from the orders carrying it, and the names to put in the header.
/// </para>
/// </summary>
public sealed class ShiftReportService
{
    private readonly AppServices _app;

    public ShiftReportService(AppServices app) => _app = app;

    public ShiftReport Build(Shift shift, ShiftReportKind kind, DateTimeOffset? printedAt = null)
    {
        var settings = _app.GetSettings();

        // Names are resolved once and cached for the report. A shift can carry
        // two staff ids and a deactivated member still has to be named — their
        // name is on every order they took.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string NameOf(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "—";
            if (names.TryGetValue(id, out var cached)) return cached;
            var found = _app.Staff.GetById(id)?.Name ?? "(removed)";
            names[id] = found;
            return found;
        }

        return ShiftReportBuilder.Build(
            shift,
            _app.Shifts.GetTotals(shift),
            _app.Orders.GetForShift(shift.Id),
            _app.Menu.GetTaxClasses(),
            kind,
            NameOf,
            settings.DefaultTaxClassId,
            settings.PricesIncludeTax,
            printedAt);
    }

    /// <summary>
    /// A reading for the shift that is open now, or null when none is.
    /// <para>
    /// An X is always safe: it reads rows and writes nothing, so it can be taken
    /// as often as anyone likes without affecting the close.
    /// </para>
    /// </summary>
    public ShiftReport? BuildCurrentX() =>
        _app.Session.Shift is { Status: ShiftStatus.Open } open
            ? Build(open, ShiftReportKind.X)
            : null;

    /// <summary>
    /// The closing account of a shift. Reproducible: a closed shift's rows do
    /// not change, so reprinting a Z years later gives the same paper.
    /// </summary>
    public ShiftReport BuildZ(Shift shift) => Build(shift, ShiftReportKind.Z);
}
