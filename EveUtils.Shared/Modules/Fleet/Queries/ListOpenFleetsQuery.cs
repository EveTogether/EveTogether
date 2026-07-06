using EveUtils.Shared.Cqrs;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Active, publicly listable fleets on this server.</summary>
public sealed record ListOpenFleetsQuery : IQuery<IReadOnlyList<FleetEntity>>;
