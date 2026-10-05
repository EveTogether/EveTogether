namespace EveUtils.Shared.Modules.Map.Enums;

/// <summary>What a planned route optimises for — the same three choices the in-game autopilot offers.</summary>
public enum RoutePreference
{
    Shortest,

    /// <summary>Stays in highsec wherever a highsec way exists, at the cost of more jumps.</summary>
    Safer,

    /// <summary>Stays out of highsec wherever it can.</summary>
    LessSecure
}
