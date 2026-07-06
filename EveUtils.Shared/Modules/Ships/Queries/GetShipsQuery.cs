using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Ships.Dtos;

namespace EveUtils.Shared.Modules.Ships.Queries;

public sealed record GetShipsQuery : IQuery<IReadOnlyList<ShipDto>>;
