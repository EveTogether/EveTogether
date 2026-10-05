namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// One activity on the clock right now — one per group of runs flown together. ISK is what the whole activity earned so
/// far, as stored: bounty as it is paid, loot as it is captured. <see cref="Signature"/> and <see cref="System"/> are
/// null unless "Include my location" is on (and OPSEC is off). Public, versioned DTO.
/// </summary>
/// <param name="Kind">abyssal, site, mission or mining.</param>
/// <param name="Type">The TYPE the app shows, e.g. "Combat Site" or "Abyssal".</param>
/// <param name="StartedAtUtc">The clock's anchor: a widget counts up (or an abyssal down) from here itself.</param>
/// <param name="Kills">The bounty lines of the activity so far, one per NPC kill; a kill that paid no bounty is not counted.</param>
/// <param name="TotalIsk">Null while nothing of it can be valued yet.</param>
public sealed record CurrentRunDto(
    Guid RunId,
    string? GroupCode,
    string Kind,
    string Type,
    string? Site,
    int? AbyssalTier,
    string? AbyssalTierName,
    string? AbyssalWeather,
    DateTime StartedAtUtc,
    decimal BountyIsk,
    int Kills,
    decimal? LootIsk,
    decimal? TotalIsk,
    IReadOnlyList<CurrentRunCrewDto> Crew,
    string? Signature,
    string? System);
