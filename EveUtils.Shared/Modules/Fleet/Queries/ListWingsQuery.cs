using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Lists a fleet's wings in insertion order.</summary>
public sealed record ListWingsQuery(long FleetId) : IQuery<IReadOnlyList<FleetWing>>;
