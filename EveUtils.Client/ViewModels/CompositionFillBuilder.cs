using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Builds the two-level composition fill overview from a coupled doctrine and a fleet's members: per
/// role-group the group-minimum tally (e.g. "24 / 40") and the per-fit minima for the entries that set one. A role
/// with neither a group minimum nor any per-fit minimum is omitted (nothing to fill against). Shared by the roster
/// (the coupled fleet) and the Fleets browser (discoverable fleets, stream B / B-1) so the fill is computed one way.
/// </summary>
public static class CompositionFillBuilder
{
    public static IReadOnlyList<CompositionFillRoleViewModel> Build(
        FleetCompositionDetail? composition, IReadOnlyList<FleetMemberInfo> members)
    {
        if (composition is null)
            return [];

        var fills = new List<CompositionFillRoleViewModel>();
        foreach (var role in composition.Roles)
        {
            var entryIds = role.Entries.Select(e => e.Id).ToHashSet();
            var groupFilled = members.Count(m => m.AssignedCompositionEntryId is { } id && entryIds.Contains(id));
            var groupCount = role.GroupMinCount is { } min ? $"{groupFilled} / {min}" : null;

            var entryFills = role.Entries
                .Where(e => e.EntryMinCount is not null)
                .Select(e => new CompositionFillEntryViewModel(
                    e.Fit.FitName,
                    $"{members.Count(m => m.AssignedCompositionEntryId == e.Id)} / {e.EntryMinCount}"))
                .ToList();

            if (groupCount is not null || entryFills.Count > 0)
                fills.Add(new CompositionFillRoleViewModel(role.RoleName, groupCount, entryFills));
        }
        return fills;
    }

    /// <summary>Whether every group minimum and per-fit minimum of the doctrine is currently met by the fleet's
    /// members (stream B / B-5). True when there is no coupled composition (nothing to fall short of) — used
    /// to warn (not block) on Start when the fleet is under-strength.</summary>
    public static bool AllMinimaMet(FleetCompositionDetail? composition, IReadOnlyList<FleetMemberInfo> members)
    {
        if (composition is null)
            return true;

        foreach (var role in composition.Roles)
        {
            var entryIds = role.Entries.Select(e => e.Id).ToHashSet();
            if (role.GroupMinCount is { } groupMin &&
                members.Count(m => m.AssignedCompositionEntryId is { } id && entryIds.Contains(id)) < groupMin)
                return false;

            foreach (var entry in role.Entries.Where(e => e.EntryMinCount is not null))
                if (members.Count(m => m.AssignedCompositionEntryId == entry.Id) < entry.EntryMinCount)
                    return false;
        }
        return true;
    }
}
