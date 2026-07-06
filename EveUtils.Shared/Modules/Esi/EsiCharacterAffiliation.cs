namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// A character's resolved public identity: its own name plus corp + alliance name/ticker (all from public ESI,
/// no token). Any field is null when that part could not be resolved; a non-null <see cref="CharacterName"/>
/// means the character itself exists.
/// </summary>
public sealed record EsiCharacterAffiliation(
    string? CharacterName,
    string? CorporationName,
    string? CorporationTicker,
    string? AllianceName,
    string? AllianceTicker);
