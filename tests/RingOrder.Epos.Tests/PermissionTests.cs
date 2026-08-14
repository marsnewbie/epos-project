using RingOrder.Epos.Domain;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// Who may do what. These read like the shop floor because that is where the
/// rules come from: a cashier takes orders, a supervisor undoes them, and only
/// the owner changes how the till works.
/// </summary>
public class PermissionTests
{
    [Theory]
    [InlineData(StaffRole.Cashier, Permission.TakeOrders, true)]
    [InlineData(StaffRole.Cashier, Permission.Discount, false)]
    [InlineData(StaffRole.Cashier, Permission.VoidOrder, false)]
    [InlineData(StaffRole.Cashier, Permission.ReopenPaidOrder, false)]
    [InlineData(StaffRole.Cashier, Permission.OpenDrawerWithoutSale, false)]
    [InlineData(StaffRole.Supervisor, Permission.Discount, true)]
    [InlineData(StaffRole.Supervisor, Permission.VoidOrder, true)]
    [InlineData(StaffRole.Supervisor, Permission.Refund, true)]
    [InlineData(StaffRole.Supervisor, Permission.CloseShift, true)]
    [InlineData(StaffRole.Supervisor, Permission.EditSettings, false)]
    [InlineData(StaffRole.Supervisor, Permission.ManageStaff, false)]
    [InlineData(StaffRole.Manager, Permission.EditSettings, true)]
    [InlineData(StaffRole.Manager, Permission.ManageStaff, true)]
    public void Role_grants_only_what_it_should(StaffRole role, Permission permission, bool allowed)
    {
        Assert.Equal(allowed, Permissions.Allows(role, permission));
    }

    [Fact]
    public void Every_permission_is_reachable_by_someone()
    {
        // A permission no role holds is an action nobody in the shop can take.
        foreach (var permission in Enum.GetValues<Permission>())
            Assert.True(Enum.GetValues<StaffRole>().Any(r => Permissions.Allows(r, permission)),
                $"nobody can {permission}");
    }

    [Fact]
    public void A_pin_is_never_recoverable_from_what_is_stored()
    {
        var (hash, salt) = PinHasher.Hash("4321");

        Assert.DoesNotContain("4321", hash);
        Assert.DoesNotContain("4321", salt);
        Assert.True(PinHasher.Verify("4321", hash, salt));
        Assert.False(PinHasher.Verify("1234", hash, salt));
    }

    [Fact]
    public void The_same_pin_hashes_differently_for_two_people()
    {
        // Shared salt would make "these two use the same PIN" visible in the file.
        var (firstHash, _) = PinHasher.Hash("1234");
        var (secondHash, _) = PinHasher.Hash("1234");
        Assert.NotEqual(firstHash, secondHash);
    }
}
