using CommunityToolkit.Mvvm.ComponentModel;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.ViewModels;

/// <summary>One row of the staff list in Settings.</summary>
public partial class StaffRow : ObservableObject
{
    public StaffRow(StaffMember member, bool isCurrent)
    {
        Member = member;
        IsCurrent = isCurrent;
        _selectedRole = member.Role;
    }

    public StaffMember Member { get; }
    public bool IsCurrent { get; }

    public string Name => Member.Name;
    public bool IsActive => Member.IsActive;

    /// <summary>Shown beside the name: the state someone needs to act on.</summary>
    public string Status => !Member.IsActive
        ? "Off"
        : Member.MustChangePin
            ? "PIN not changed"
            : IsCurrent ? "Signed in" : "";

    public bool HasStatus => Status.Length > 0;
    public bool NeedsAttention => Member.IsActive && Member.MustChangePin;
    public string ActiveLabel => Member.IsActive ? "Switch off" : "Switch on";

    [ObservableProperty] private StaffRole _selectedRole;
}
