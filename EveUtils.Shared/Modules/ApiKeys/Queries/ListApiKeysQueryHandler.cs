using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Repositories;

namespace EveUtils.Shared.Modules.ApiKeys.Queries;

internal sealed class ListApiKeysQueryHandler(IApiKeyRepository repository)
    : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>
{
    public async Task<IReadOnlyList<ApiKeyDto>> Handle(ListApiKeysQuery query, CancellationToken cancellationToken = default)
    {
        var keys = await repository.ListAsync(cancellationToken);
        return [.. keys.Select(key => key.ToDto())];
    }
}
