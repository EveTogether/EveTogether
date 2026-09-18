using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The rows a server tab shows, built from the runs that server handed back (ET-311) exactly as
/// <see cref="GetActivityOverviewQuery"/> builds Local's from the stored summaries — same grouping, same pricing, same
/// own-share split — without storing any of it. <paramref name="FleetId"/> and <paramref name="OwnCharacterIds"/> mean
/// what they mean there; the fleet filter reads this client's own <c>RunGroupOrigin</c>, the only record of it.</summary>
public sealed record GetServerActivityOverviewQuery(
    string ServerAddress,
    IReadOnlyList<RunWirePayload> Runs,
    long? FleetId = null,
    IReadOnlyList<long>? OwnCharacterIds = null) : IQuery<Result<IReadOnlyList<ServerActivityDto>>>;
