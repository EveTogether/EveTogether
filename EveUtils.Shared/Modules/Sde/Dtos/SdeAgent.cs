namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// An NPC agent from the SDE (ET-173) — the mission-giving character behind an <c>npcCharacters</c> row that
/// carries an <c>agent</c> sub-object (10.966 of 11.393 rows measured, build 3492266; the rest are generic NPCs
/// such as corporation CEOs and are not agents).
/// </summary>
/// <param name="SolarSystemId">Resolved through the agent's station (<paramref name="LocationId"/>) via
/// <c>npcStations.jsonl</c>; null when the store has no import of that station.</param>
/// <param name="SolarSystemName">Null under the same condition as <paramref name="SolarSystemId"/>.</param>
public sealed record SdeAgent(
    int AgentId,
    string Name,
    int Level,
    int AgentTypeId,
    string? AgentTypeName,
    int DivisionId,
    bool IsLocator,
    int CorporationId,
    int LocationId,
    int? SolarSystemId,
    string? SolarSystemName);
