namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// One of your own kills or losses, as <c>GET /killmails/latest</c> returns it and as the <c>killmail.added</c> push
/// carries it. <c>Kind</c> is <c>Kill</c> or <c>Loss</c>; <c>ShipTypeId</c>/<c>ShipName</c> is the victim's ship.
/// Names are ESI-resolved (cached); an id ESI cannot name comes back as the bare id. <c>SolarSystemId</c> and
/// <c>SolarSystemName</c> are null unless "Include my location" is on.
/// </summary>
public sealed record KillmailLatestDto(
    int KillmailId,
    string Kind,
    int CharacterId,
    string CharacterName,
    DateTime KillmailTimeUtc,
    int ShipTypeId,
    string ShipName,
    decimal? IskValue,
    int AttackerCount,
    Guid? RunId,
    string? VictimName,
    string? VictimCorporationName,
    KillmailFinalBlowNameDto? FinalBlow,
    int? SolarSystemId,
    string? SolarSystemName);

public sealed record KillmailFinalBlowNameDto(string? CharacterName, string? CorporationName, int? ShipTypeId, string? ShipName);
