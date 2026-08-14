using RingOrder.Epos.Data;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>
/// Who is signed in and which trading session their work belongs to.
/// <para>
/// Every order and payment is stamped from here. That stamping is the whole
/// point: without it, "the drawer is £20 short" has no answer, and no report
/// can be broken down by the person who took the money.
/// </para>
/// </summary>
public sealed class PosSession
{
    private readonly StaffRepository _staff;
    private readonly ShiftRepository _shifts;
    private readonly AuditRepository _audit;

    public PosSession(StaffRepository staff, ShiftRepository shifts, AuditRepository audit)
    {
        _staff = staff;
        _shifts = shifts;
        _audit = audit;
        Shift = _shifts.GetOpen();
    }

    public StaffMember? Staff { get; private set; }
    public Shift? Shift { get; private set; }

    public bool IsSignedIn => Staff is not null;
    public bool HasOpenShift => Shift is { Status: ShiftStatus.Open };

    /// <summary>Identifies this till. One for now; the column exists for the second.</summary>
    public string TerminalId { get; } = Environment.MachineName;

    public event EventHandler? Changed;

    public StaffMember? SignIn(string pin)
    {
        var member = _staff.Authenticate(pin);
        if (member is null) return null;

        Staff = member;
        Shift = _shifts.GetOpen();
        Record("staff.signin", member.Id, member.Name);
        Changed?.Invoke(this, EventArgs.Empty);
        return member;
    }

    public void SignOut()
    {
        if (Staff is { } member) Record("staff.signout", member.Id, member.Name);
        Staff = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Shift OpenShift(decimal openingFloat)
    {
        if (Staff is null) throw new InvalidOperationException("Sign in before opening a shift.");
        Shift = _shifts.Open(Staff.Id, openingFloat, TerminalId);
        Record("shift.open", Shift.Id, $"#{Shift.Number} float {openingFloat:0.00}");
        Changed?.Invoke(this, EventArgs.Empty);
        return Shift;
    }

    public (ShiftTotals Totals, decimal Variance) CloseShift(decimal declaredCash, string? notes)
    {
        if (Staff is null) throw new InvalidOperationException("Sign in before closing a shift.");
        if (Shift is not { Status: ShiftStatus.Open } open)
            throw new InvalidOperationException("No shift is open.");

        var totals = _shifts.GetTotals(open);
        _shifts.Close(open, Staff.Id, declaredCash, totals.ExpectedCash, notes);
        var variance = Money.Round(declaredCash - totals.ExpectedCash);
        Record("shift.close", open.Id, $"#{open.Number} declared {declaredCash:0.00} variance {variance:0.00}");

        Shift = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return (totals, variance);
    }

    public ShiftTotals? CurrentTotals() => Shift is null ? null : _shifts.GetTotals(Shift);

    /// <summary>
    /// Marks an order with who took it and where it counts. Applied when the
    /// ticket is first persisted and never rewritten, so reopening yesterday's
    /// order does not move its money into today's shift.
    /// </summary>
    public void Stamp(PosOrder order)
    {
        order.StaffId ??= Staff?.Id;
        order.ShiftId ??= Shift?.Id;
        order.TerminalId ??= TerminalId;
    }

    /// <summary>Stamps a payment with the person taking it, right now.</summary>
    public void Stamp(OrderTender tender) => tender.StaffId ??= Staff?.Id;

    public bool Can(Permission permission) => Staff?.Can(permission) ?? false;

    public void Record(string action, string? subjectId = null, string? detail = null) =>
        _audit.Record(new AuditEntry
        {
            StaffId = Staff?.Id,
            ShiftId = Shift?.Id,
            Action = action,
            SubjectId = subjectId,
            Detail = detail,
        });
}
