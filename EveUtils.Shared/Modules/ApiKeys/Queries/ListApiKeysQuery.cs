using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.ApiKeys.Dtos;

namespace EveUtils.Shared.Modules.ApiKeys.Queries;

/// <summary>All API keys, newest first, as metadata only.</summary>
public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKeyDto>>;
