using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>A fleet's still-pending invites — backs the roster's pending-invites section.</summary>
public sealed record ListPendingFleetInvitesQuery(long FleetId) : IQuery<IReadOnlyList<FleetInvite>>;
