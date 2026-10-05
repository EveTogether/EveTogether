namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>Why the server did not pass a member's fleet traffic on (ET-440).</summary>
public enum FleetRelayRefusal
{
    /// <summary>The sending character is not on the fleet's roster.</summary>
    NotOnRoster = 1,

    /// <summary>The fleet has not been started, so it broadcasts nothing yet.</summary>
    FleetNotStarted = 2,
}
