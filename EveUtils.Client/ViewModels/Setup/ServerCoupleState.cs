namespace EveUtils.Client.ViewModels.Setup;

/// <summary>Where the optional server coupling of the server step stands.</summary>
public enum ServerCoupleState
{
    Idle,
    Checking,
    Reachable,
    Unreachable,
    Pairing,
    Coupled,

    /// <summary>The EVE login for the server was not authorized.</summary>
    PairingCancelled,

    /// <summary>Another character signed in on the EVE page than the one this step couples.</summary>
    OtherCharacter,

    /// <summary>Any other failure; the message says what.</summary>
    PairingFailed
}
