namespace EveUtils.Shared.Modules.ServerAuth.Dtos;

public sealed record SyncedCharacterDto(int Id, int EsiCharacterId, string CharacterName, DateTimeOffset PairedAt);
