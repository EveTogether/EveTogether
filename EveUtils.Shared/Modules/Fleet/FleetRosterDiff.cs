using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// The diff between the live in-game roster and our planned (doctrine) roster: who joined, who is still
/// missing, and who is in-game but not in our plan. Keyed on character id — position (wing/squad) diffing waits until
/// maps our ids onto the in-game ids. Drives the "who joined / who's missing vs the doctrine" view.
/// </summary>
public sealed record FleetRosterDiff(
    IReadOnlyList<int> Present,
    IReadOnlyList<int> Missing,
    IReadOnlyList<int> External);

/// <summary>Computes a <see cref="FleetRosterDiff"/> from the planned vs live character-id sets (pure).</summary>
public static class FleetRosterDiffer
{
    public static FleetRosterDiff Diff(IEnumerable<int> plannedCharacterIds, IEnumerable<int> liveCharacterIds)
    {
        var planned = plannedCharacterIds.ToHashSet();
        var live = liveCharacterIds.ToHashSet();
        return new FleetRosterDiff(
            Present: planned.Where(live.Contains).OrderBy(id => id).ToList(),
            Missing: planned.Where(id => !live.Contains(id)).OrderBy(id => id).ToList(),
            External: live.Where(id => !planned.Contains(id)).OrderBy(id => id).ToList());
    }
}
