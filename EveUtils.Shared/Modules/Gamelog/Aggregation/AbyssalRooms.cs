using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>Whose the enemies in a room were. <see cref="Unknown"/> is a reading, not a failure: the vocabulary is
/// provably incomplete, and a room nothing can name has to say so instead of borrowing a neighbour's answer.</summary>
public enum AbyssalFaction
{
    Unknown,
    Triglavian,
    RogueDrones,
    Sleepers,
    Sansha
}

/// <summary><see cref="EnemyNames"/> holds every name whose window touches the room, which is not the same as every
/// name that fenced it — a name seen across a boundary is listed in each room it reaches rather than assigned to
/// one, because which one it belonged to is exactly what could not be established.</summary>
public sealed record AbyssalRoom(
    int Number, DateTime StartedAtUtc, DateTime EndedAtUtc, AbyssalFaction Faction, IReadOnlyList<string> EnemyNames);

/// <summary>What one run's enemy observations say about its rooms. An empty <see cref="Rooms"/> means two different
/// things and they must not be confused: with <see cref="RoomsUnknown"/> false there is nothing to report, and with
/// it true the run was abyssal but its rooms could not be told apart. Saying the second out loud is the point —
/// pointing at the wrong room in silence is worse than pointing at none.</summary>
public sealed record AbyssalRoomReading(IReadOnlyList<AbyssalRoom> Rooms, bool RoomsUnknown);

/// <summary>
/// The rooms of one abyssal run, read back out of ET-106's enemy observations. No second parse of the gamelog: the
/// stored windows are the only evidence, and they are aggregated per enemy name.
///
/// That aggregation is why the boundary cannot simply be "windows that overlap". A name shot in more than one room
/// carries one window spanning all of them, and the stored form cannot say whether it was seen continuously or in
/// separate bursts — so such a name is no evidence of a boundary, and is left out of the fence rather than allowed
/// to weld the run into one piece. The gap in the log is never used to find a boundary, only to drop the weakest of
/// the ones already found.
/// </summary>
public static class AbyssalRooms
{
    /// <summary>A filament opens three rooms and the pilot must clear all three, so this is a count and not a
    /// guess. Fewer than three candidates means the division failed, not that the run was short.</summary>
    public const int RoomsPerRun = 3;

    /// <summary>
    /// [gemeten] on two of Raymond's own runs of 2026-08-29 and on nothing else. In both, the Biocombinative Cache
    /// was the only bridging name. A third run that puts two bridges side by side will report its rooms unknown
    /// rather than split them — that is this rule's known edge, not a surprise.
    /// </summary>
    public static AbyssalRoomReading Detect(int? solarSystemId, IEnumerable<RunEnemyObservationDto> observations)
    {
        if (solarSystemId is not { } system || !AbyssalSpace.IsAbyssalSystem(system))
            return new AbyssalRoomReading([], RoomsUnknown: false);

        var all = observations.OrderBy(observation => observation.FirstObservedAtUtc).ToList();
        var seen = Group(all).Count;

        // Value inequality is reference inequality here: the collector keeps one row per type id and a saved run
        // cannot be saved again, so one run holds no two alike rows. No schema constraint backs that — then go by index.
        // ponytail: O(n²) bridge test — a run carries a handful of names; index the windows if that ever changes.
        var fenced = Group(all.Where(observation => Group(all.Where(other => other != observation)).Count <= seen));
        if (fenced.Count < RoomsPerRun)
            return new AbyssalRoomReading([], RoomsUnknown: true);

        while (fenced.Count > RoomsPerRun)
            MergeNarrowestBoundary(fenced);

        return new AbyssalRoomReading(
            [
                .. fenced.Select((room, index) =>
                {
                    var startedAtUtc = room.Min(observation => observation.FirstObservedAtUtc);
                    var endedAtUtc = room.Max(observation => observation.LastObservedAtUtc);

                    return new AbyssalRoom(
                        index + 1,
                        startedAtUtc,
                        endedAtUtc,
                        FactionOf(room),
                        [
                            .. all
                                .Where(observation => observation.FirstObservedAtUtc <= endedAtUtc
                                                      && observation.LastObservedAtUtc >= startedAtUtc)
                                .Select(observation => observation.EnemyName)
                        ]);
                })
            ],
            RoomsUnknown: false);
    }

    /// <summary>Windows that touch, folded into one group. Ordered by first sighting, so the running end only ever
    /// moves forward and a group closes the moment a window starts after it.</summary>
    private static List<List<RunEnemyObservationDto>> Group(IEnumerable<RunEnemyObservationDto> observations)
    {
        var groups = new List<List<RunEnemyObservationDto>>();
        var end = DateTime.MinValue;

        foreach (var observation in observations.OrderBy(observation => observation.FirstObservedAtUtc))
        {
            if (groups.Count == 0 || observation.FirstObservedAtUtc > end)
                groups.Add([]);

            groups[^1].Add(observation);
            if (observation.LastObservedAtUtc > end)
                end = observation.LastObservedAtUtc;
        }

        return groups;
    }

    /// <summary>Drop the shortest of the found boundaries. Killing one enemy before pulling the next leaves a gap
    /// inside a room too (measured at 7 s against transitions of 19, 26, 58 and 83 s), so more than three groups
    /// means the extra boundaries are the weakest ones — which of them is decided by comparison, never by a
    /// threshold that would have to hold on runs nobody has seen.</summary>
    private static void MergeNarrowestBoundary(List<List<RunEnemyObservationDto>> groups)
    {
        var narrowest = Enumerable.Range(1, groups.Count - 1)
            .MinBy(index => groups[index].Min(observation => observation.FirstObservedAtUtc)
                            - groups[index - 1].Max(observation => observation.LastObservedAtUtc));

        groups[narrowest - 1].AddRange(groups[narrowest]);
        groups.RemoveAt(narrowest);
    }

    /// <summary>The faction of the names that fenced the room, and only of those. One known faction names the room;
    /// none or several leaves it unknown, because the source itself warns that spawns are mixed. A name the table
    /// does not carry is no evidence either way and does not veto the ones that are.</summary>
    private static AbyssalFaction FactionOf(IEnumerable<RunEnemyObservationDto> members)
    {
        var known = members
            .Select(member => FactionByPrefix.GetValueOrDefault(member.EnemyName.Split(' ')[0]))
            .Where(faction => faction != AbyssalFaction.Unknown)
            .Distinct()
            .ToList();

        return known.Count == 1 ? known[0] : AbyssalFaction.Unknown;
    }

    /// <summary>
    /// The adjective an abyssal NPC is named with, which is the part that carries the faction — the hull behind it
    /// does not, and the loot structures carry the faction word itself ("Triglavian Biocombinative Cache") in every
    /// room regardless of whose room it is, so they are deliberately absent here.
    ///
    /// [gemeten] on Raymond's runs of 2026-08-29 and held to those. The wiki offers more prefixes but warns it is
    /// outdated and that Triglavian and Rogue Drone spawns swap, and it calls the Ephialtes and Tyrannos hulls
    /// Sleeper in one place and Drifter in another — an unresolved reading is left out rather than picked.
    /// </summary>
    private static readonly Dictionary<string, AbyssalFaction> FactionByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Striking"] = AbyssalFaction.Triglavian,
        ["Sparkneedle"] = AbyssalFaction.RogueDrones,
        ["Photic"] = AbyssalFaction.RogueDrones,
        ["Lucid"] = AbyssalFaction.Sleepers,
        ["Devoted"] = AbyssalFaction.Sansha
    };
}
