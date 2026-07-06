using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Repositories;

namespace EveUtils.Shared.Modules.Gamelog.Queries;

internal sealed class GetRecentCombatQueryHandler(IGamelogRepository repository, IPrincipalAccessor principals)
    : IQueryHandler<GetRecentCombatQuery, IReadOnlyList<CombatSampleDto>>
{
    public async Task<IReadOnlyList<CombatSampleDto>> Handle(GetRecentCombatQuery query, CancellationToken cancellationToken = default)
    {
        var samples = await repository.RecentAsync(principals.Current.OwnerId, query.Take, cancellationToken);
        return samples
            .Select(s => new CombatSampleDto(s.Id, s.CharacterId, s.Amount, s.Direction, s.Target, s.Timestamp))
            .ToList();
    }
}
