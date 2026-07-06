using EveUtils.Shared.Cqrs;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>The active fleets the character is involved in — owns or is a roster member of.</summary>
public sealed record ListMyFleetsQuery(int CharacterId) : IQuery<IReadOnlyList<FleetEntity>>;
