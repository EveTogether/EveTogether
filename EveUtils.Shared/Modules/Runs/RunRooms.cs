using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>Reads a run's rooms back out of its <see cref="RunParameterKey.RoomStarted"/> boundaries (ET-240) — the
/// one place that decides which room a moment falls in, so the run window, the detail screen and anything reading
/// rooms later (ET-469) cannot split a run differently. Room 1 starts with the run and has no boundary of its own.</summary>
public static class RunRooms
{
    /// <summary>One run's boundaries, oldest first — empty for a run nobody pressed NEW ROOM on.</summary>
    public static IReadOnlyList<DateTime> Boundaries(IEnumerable<RunParameterDto> parameters, Guid runId) =>
        [.. parameters
            .Where(parameter => parameter.RunId == runId && parameter.ParameterKey == RunParameterKey.RoomStarted)
            .Select(parameter => parameter.ObservedAtUtc)
            .Order()];

    /// <summary>The room <paramref name="atUtc"/> falls in, or null when the run has no rooms. A moment on a boundary
    /// is the new room's; before the run started is room 1, after it stopped the last room.</summary>
    public static int? RoomOf(IReadOnlyList<DateTime> boundaries, DateTime atUtc) =>
        boundaries.Count == 0 ? null : 1 + boundaries.Count(boundary => boundary <= atUtc);

    /// <summary>When room <paramref name="room"/> began: the run's start for room 1, its own boundary after that.</summary>
    public static DateTime StartOf(IReadOnlyList<DateTime> boundaries, int room, DateTime runStartedAtUtc) =>
        room <= 1 ? runStartedAtUtc : boundaries[room - 2];

    /// <summary>When room <paramref name="room"/> ended: the next boundary, or the run's stop for the last room —
    /// null while that room is still going.</summary>
    public static DateTime? EndOf(IReadOnlyList<DateTime> boundaries, int room, DateTime? runStoppedAtUtc) =>
        room <= boundaries.Count ? boundaries[room - 1] : runStoppedAtUtc;
}
