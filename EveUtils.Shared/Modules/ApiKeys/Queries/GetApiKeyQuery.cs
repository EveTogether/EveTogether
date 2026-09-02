using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.ApiKeys.Dtos;

namespace EveUtils.Shared.Modules.ApiKeys.Queries;

/// <summary>One API key as metadata; the plaintext is gone after creation and never comes back.</summary>
public sealed record GetApiKeyQuery(int ApiKeyId) : IQuery<ApiKeyDto?>;
