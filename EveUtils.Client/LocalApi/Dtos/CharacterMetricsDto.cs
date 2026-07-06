namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// Live combat metrics for one of your own running characters, sourced from the local gamelog (always-on, fleet
/// independent). Rates are per second. Public, versioned DTO — never carries tokens or scopes.
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
    double PeakDps);
