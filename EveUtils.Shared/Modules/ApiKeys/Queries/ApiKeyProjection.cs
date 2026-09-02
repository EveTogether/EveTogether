using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Entities;

namespace EveUtils.Shared.Modules.ApiKeys.Queries;

/// <summary>The single place an <see cref="ApiKey"/> becomes something the panel may see — so there is one
/// line to review for the rule that the secret hash never leaves the module.</summary>
internal static class ApiKeyProjection
{
    public static ApiKeyDto ToDto(this ApiKey key) => new(
        key.Id,
        key.Label,
        key.Prefix,
        key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        key.OwnerCharacterId,
        key.IsActive,
        key.CreatedAt,
        key.CreatedBy,
        key.LastUsedAt,
        key.ExpiresAt);
}
