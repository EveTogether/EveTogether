namespace EveUtils.Shared.Modules.ApiKeys.Dtos;

/// <summary>An API key as the panel may see it: metadata only. The secret hash is never projected here.</summary>
public sealed record ApiKeyDto(
    int Id,
    string Label,
    string Prefix,
    IReadOnlyList<string> Scopes,
    int? OwnerCharacterId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt);
