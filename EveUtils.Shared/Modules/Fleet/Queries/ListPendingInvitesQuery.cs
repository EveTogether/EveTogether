using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>A character's still-pending invites — backs the on-attach durable sync.</summary>
public sealed record ListPendingInvitesQuery(int InviteeCharacterId) : IQuery<IReadOnlyList<FleetInvite>>;
