using EveUtils.Shared.Cqrs;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>The active fleets the character is involved in — owns or is a roster member of. Concluded fleets are
/// left out unless asked for: only the overview's FINISHED band wants them (ET-170).</summary>
public sealed record ListMyFleetsQuery(int CharacterId, bool IncludeConcluded = false) : IQuery<IReadOnlyList<FleetEntity>>;
