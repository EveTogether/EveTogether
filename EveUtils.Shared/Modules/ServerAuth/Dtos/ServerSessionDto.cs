namespace EveUtils.Shared.Modules.ServerAuth.Dtos;

public sealed record ServerSessionDto(
    int Id,
    int SyncedCharacterId,
    string CharacterName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastHeartbeat);
