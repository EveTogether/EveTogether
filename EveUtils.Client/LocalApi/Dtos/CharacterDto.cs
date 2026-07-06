namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A coupled character's public identity for the local API: name, corp/alliance affiliation, a public portrait URL
/// and whether an EVE client for it is running. Public, versioned DTO — never carries ESI tokens or granted scopes.
/// </summary>
public sealed record CharacterDto(
    int? Id,
    string Name,
    bool Running,
    string? CorporationName,
    string? CorporationTicker,
    string? AllianceName,
    string? AllianceTicker,
    string? PortraitUrl);
