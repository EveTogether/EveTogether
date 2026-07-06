using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Lists a wing's squads in insertion order.</summary>
public sealed record ListSquadsQuery(long WingId) : IQuery<IReadOnlyList<FleetSquad>>;
