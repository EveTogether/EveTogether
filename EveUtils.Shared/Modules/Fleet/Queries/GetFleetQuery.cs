using EveUtils.Shared.Cqrs;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>Reads a single fleet by id (null if it does not exist).</summary>
public sealed record GetFleetQuery(long FleetId) : IQuery<FleetEntity?>;
