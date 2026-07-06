namespace EveUtils.Shared.Modules.ServerAuth.Dtos;

public sealed record AllowedCharacterDto(int Id, int? EsiCharacterId, string CharacterName, string? Note);
