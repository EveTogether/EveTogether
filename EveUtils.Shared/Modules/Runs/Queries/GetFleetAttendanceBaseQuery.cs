using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>
/// Where the next homefront of a series starts from (ET-230; Jithran, 2026-09-11: homefronts are flown several in a
/// row, so whoever was ticked at site 1 should stand ticked at site 2): the final list of the most recent homefront
/// decided in <paramref name="FleetId"/>, leaving out <paramref name="ExcludeGroupCode"/> — the site being flown now. Read from the store rather than from memory, so a series survives a restart in between.
///
/// "Same fleet" is <c>RunGroupOrigin</c>'s, the one route from a fleet to its groups (ET-182); a group this client never
/// recorded an origin for is not part of any series. Null when there is no earlier homefront to start from.
/// </summary>
public sealed record GetFleetAttendanceBaseQuery(long FleetId, string? ExcludeGroupCode)
    : IQuery<Result<RunAttendanceDecision?>>;
