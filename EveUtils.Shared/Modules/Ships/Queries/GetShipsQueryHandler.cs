using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Ships.Dtos;
using EveUtils.Shared.Modules.Ships.Repositories;

namespace EveUtils.Shared.Modules.Ships.Queries;

internal sealed class GetShipsQueryHandler(IShipRepository repository)
    : IQueryHandler<GetShipsQuery, IReadOnlyList<ShipDto>>
{
    public async Task<IReadOnlyList<ShipDto>> Handle(GetShipsQuery query, CancellationToken cancellationToken = default)
    {
        var ships = await repository.ListAsync(cancellationToken);
        return ships.Select(s => new ShipDto(s.Id, s.Name, s.Class, s.Mass)).ToList();
    }
}
