using EveUtils.Shared.Cqrs;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Lists the fleets a character owns.</summary>
public sealed record ListFleetsByCreatorQuery(int CreatorCharacterId) : IQuery<IReadOnlyList<FleetEntity>>;
