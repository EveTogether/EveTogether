using System.Collections.Generic;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A row that stands for one fleet member and can therefore carry the shared member context menu. A row mounts it
/// with <c>ItemsSource="{Binding MemberMenu}"</c> plus the app-level <c>FleetMemberMenuItemTheme</c>; implementing
/// this is what makes that one binding name a contract rather than a coincidence, whatever screen draws the row.
/// </summary>
public interface IFleetMemberMenuHost
{
    /// <summary>The menu for this member: information lines first, actions last. Empty means no menu.</summary>
    IReadOnlyList<FleetMemberMenuItemViewModel> MemberMenu { get; }
}
