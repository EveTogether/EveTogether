using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>The one set of rooms a group's detail draws (ET-494), so ENEMIES and ROOMS (ET-469) cannot split a run
/// differently: the commander's run when it has rooms, else the fullest list, the earliest run on a tie.</summary>
internal sealed record RunRoomSet(ActivityRunDetailDto Run, IReadOnlyList<DateTime> Boundaries)
{
    public static RunRoomSet? Of(ActivityDetailDto detail)
    {
        List<RunRoomSet> withRooms = [.. detail.Runs
            .OrderBy(run => run.StartedAtUtc).ThenBy(run => run.RunId)
            .Select(run => new RunRoomSet(run, RunRooms.Boundaries(detail.Parameters, run.RunId)))
            .Where(set => set.Boundaries.Count > 0)];
        return withRooms.FirstOrDefault(set => set.Run.Role is RunRole.FleetCommander) ?? withRooms.MaxBy(set => set.Boundaries.Count);
    }

    /// <summary>Every pilot's row lands in a room by its first sighting, and one type is one row per room across the
    /// pilots: the widest window, and the largest count since they saw the same spawn.</summary>
    public IEnumerable<(int Room, List<RunEnemyObservationDto> Rows)> EnemiesByRoom(ActivityDetailDto detail) =>
        detail.EnemyObservations
            .GroupBy(observation => RunRooms.RoomOf(Boundaries, observation.FirstObservedAtUtc) ?? 1)
            .OrderBy(room => room.Key)
            .Select(room => (room.Key, Rows: room.GroupBy(observation => observation.EnemyTypeId)
                .Select(type => type.First() with
                {
                    Count = type.Max(observation => observation.Count),
                    FirstObservedAtUtc = type.Min(observation => observation.FirstObservedAtUtc),
                    LastObservedAtUtc = type.Max(observation => observation.LastObservedAtUtc)
                }).ToList()));
}
