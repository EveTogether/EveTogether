namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// Live combat metrics for one of your own running characters, sourced from the local gamelog (always-on, fleet
/// independent). Rates are per second. <c>NeutPerSecond</c> and <c>CapPerSecond</c> add both directions; <c>NeutIn</c>/<c>NeutOut</c>,
/// <c>CapIn</c>/<c>CapOut</c> and <c>RepIn</c>/<c>RepOut</c> keep them apart (received / given). Public, versioned DTO — never carries tokens or scopes.
/// </summary>
public sealed record CharacterMetricsDto(
    int? CharacterId,
    string CharacterName,
    bool Running,
    double DpsOut,
    double DpsIn,
    double NeutPerSecond,
    double CapPerSecond,
    long BountyTotal,
    int Kills,
    string? Location,
    double PeakDps,
    bool LocationWatchActive = false,
    string? LocationStatus = null,
    double RepIn = 0,
    double RepOut = 0,
    double NeutIn = 0,
    double NeutOut = 0,
    double CapIn = 0,
    double CapOut = 0,
    ApplicationDto? Application = null);
