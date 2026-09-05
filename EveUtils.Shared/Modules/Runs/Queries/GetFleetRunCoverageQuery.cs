using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>How many of a fleet's runs are completed and known, as opposed to zero (ET-185). Separate from
/// <see cref="GetActivityOverviewQuery"/> because that query answers "which activities", while this one has to
/// answer a question that query cannot: whether an empty answer means the fleet flew nothing, or means the fleet is
/// older than <c>RunGroupOrigin</c> (ET-182) itself and simply was never in a position to be found.</summary>
/// <param name="FleetId">The fleet whose completed runs are being counted.</param>
/// <param name="FleetCreatedAtUtc">When the fleet itself came into being. A fleet created no earlier than the oldest
/// row <c>RunGroupOrigin</c> holds could not have flown a run before tracking started, so a zero for it is a real
/// zero. A fleet older than that floor — or a client where the table is still empty — cannot be told apart from one
/// that simply predates the record, so a zero there is reported as unknown instead.</param>
public sealed record GetFleetRunCoverageQuery(
    long FleetId, DateTime FleetCreatedAtUtc) : IQuery<Result<FleetRunCoverageDto>>;
