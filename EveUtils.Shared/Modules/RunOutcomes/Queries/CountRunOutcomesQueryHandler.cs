using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

internal sealed class CountRunOutcomesQueryHandler(IRunOutcomeReader outcomes) : IQueryHandler<CountRunOutcomesQuery, Result<int>>
{
    public async Task<Result<int>> Handle(CountRunOutcomesQuery query, CancellationToken cancellationToken = default) =>
        Result<int>.Success(await outcomes.CountAsync(cancellationToken));
}
