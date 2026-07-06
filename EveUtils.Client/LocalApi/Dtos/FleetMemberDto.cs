namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// One roster member of the active fleet: the pilot, their wing/squad placement and role, and — when a fit is
/// assigned — the ship type + resolved name and the fit name. <c>CharacterName</c> is resolved from the
/// fleet's connected-character set, null when not known locally. No skills or tokens are exposed.
/// </summary>
public sealed record FleetMemberDto(
    long Id,
    int CharacterId,
    string? CharacterName,
    long WingId,
    long SquadId,
    string Role,
    bool IsExternal,
    int? ShipTypeId,
    string? ShipName,
    string? FitName);
