namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A fleet you own or are a member of, as a directory row. <c>Scope</c> is "local" for a client-only fleet, or
/// "server" for one on a coupled server (then <c>ServerAddress</c>/<c>ServerName</c> are set). Discoverable/open
/// fleets you are not in are deliberately not listed — this surface is your own fleets only.
/// </summary>
public sealed record FleetListItemDto(
    long Id,
    string Name,
    string? Description,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    int CreatorCharacterId,
    string State,
    string Activation,
    string Visibility,
    bool IsClientOnly,
    long? CompositionId);
