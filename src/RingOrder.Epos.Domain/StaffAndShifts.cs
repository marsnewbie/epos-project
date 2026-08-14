using System.Security.Cryptography;
using System.Text;

namespace RingOrder.Epos.Domain;

public sealed class StaffMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public StaffRole Role { get; set; } = StaffRole.Cashier;

    /// <summary>Salted hash — a PIN is never stored in the clear, even locally.</summary>
    public string PinHash { get; set; } = "";
    public string PinSalt { get; set; } = "";

    /// <summary>Provisioned PINs are shared by whoever set the till up.</summary>
    public bool MustChangePin { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public bool Can(Permission permission) => Permissions.Allows(Role, permission);
}

/// <summary>
/// Actions worth stopping someone from doing. Named for the action rather than
/// the rank so a shop can be re-graded without hunting for role checks.
/// </summary>
public enum Permission
{
    TakeOrders,
    VoidSentLine,
    VoidOrder,
    Refund,

    /// <summary>Reopen a settled sale to add to it or re-tender it.</summary>
    ReopenPaidOrder,

    Discount,
    OpenDrawerWithoutSale,
    CloseShift,
    EditMenu,
    EditSettings,
    ManageStaff,
}

public static class Permissions
{
    public static bool Allows(StaffRole role, Permission permission) => role switch
    {
        StaffRole.Manager => true,
        StaffRole.Supervisor => permission is not (Permission.EditSettings or Permission.ManageStaff),
        _ => permission is Permission.TakeOrders,
    };
}

public static class PinHasher
{
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(Derive(pin, salt)), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hash, string salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
        var computed = Derive(pin, Convert.FromBase64String(salt));
        return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(hash));
    }

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// A trading session: opened with a float, closed with a count. Every order and
/// every payment carries its id, which is what makes an end-of-day report an
/// account of the drawer rather than a sum over a date range.
/// </summary>
public sealed class Shift
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Sequential per till, printed on the Z report, never reused.</summary>
    public int Number { get; set; }

    public ShiftStatus Status { get; set; } = ShiftStatus.Open;
    public string? TerminalId { get; set; }

    public string OpenedByStaffId { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Cash put in the drawer at open.</summary>
    public decimal OpeningFloat { get; set; }

    public string? ClosedByStaffId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Cash actually counted at close.</summary>
    public decimal? DeclaredCash { get; set; }

    /// <summary>Cash the till believes should be there, frozen at close.</summary>
    public decimal? ExpectedCash { get; set; }

    public string? Notes { get; set; }

    public decimal? Variance =>
        DeclaredCash is { } declared && ExpectedCash is { } expected
            ? Money.Round(declared - expected)
            : null;
}

/// <summary>Cash added to or taken out of the drawer outside a sale.</summary>
public sealed class CashMovement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShiftId { get; set; } = "";
    public string StaffId { get; set; } = "";

    /// <summary>Positive puts money in, negative takes it out.</summary>
    public decimal Amount { get; set; }

    public string Reason { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Append-only record of the things people ask about later: who voided it, who
/// discounted it, who opened the drawer at 3am.
/// </summary>
public sealed class AuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? StaffId { get; set; }
    public string? ShiftId { get; set; }

    /// <summary>Verb, e.g. <c>order.void</c>, <c>drawer.open</c>, <c>menu.price</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary>What it happened to — order id, item id, printer id.</summary>
    public string? SubjectId { get; set; }

    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}
