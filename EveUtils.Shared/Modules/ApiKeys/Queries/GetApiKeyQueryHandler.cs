using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Repositories;

namespace EveUtils.Shared.Modules.ApiKeys.Queries;

internal sealed class GetApiKeyQueryHandler(IApiKeyRepository repository)
    : IQueryHandler<GetApiKeyQuery, ApiKeyDto?>
{
    public async Task<ApiKeyDto?> Handle(GetApiKeyQuery query, CancellationToken cancellationToken = default)
    {
        var key = await repository.GetAsync(query.ApiKeyId, cancellationToken);
        return key?.ToDto();
    }
}
