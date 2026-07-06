using System.Collections.Generic;

namespace EveUtils.Client.ViewModels;

/// <summary>One role-group chip in the coupled-composition two-level fill overview: the role name, an
/// optional group-minimum tally (<paramref name="GroupCount"/>, e.g. "2 / 40" — null when the role has no group
/// minimum), and the per-fit minima for the entries that set one (e.g. "Guardian 1 / 3", "Scimitar 1 / 2"). A role
/// with neither a group minimum nor any per-fit minimum is not shown — there is nothing to fill against.</summary>
public sealed record CompositionFillRoleViewModel(
    string RoleName,
    string? GroupCount,
    IReadOnlyList<CompositionFillEntryViewModel> Entries)
{
    public bool HasGroupCount => GroupCount is not null;
    public bool HasEntries => Entries.Count > 0;
}
