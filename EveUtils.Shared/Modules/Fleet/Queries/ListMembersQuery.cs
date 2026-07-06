using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Lists a fleet's roster members in insertion order.</summary>
public sealed record ListMembersQuery(long FleetId) : IQuery<IReadOnlyList<FleetMember>>;
