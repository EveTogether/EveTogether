using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>A fleet's still-pending join requests — backs the owner's roster pending-section.</summary>
public sealed record ListPendingJoinRequestsQuery(long FleetId) : IQuery<IReadOnlyList<FleetJoinRequest>>;
