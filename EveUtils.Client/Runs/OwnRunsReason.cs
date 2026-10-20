namespace EveUtils.Client.Runs;

/// <summary>Why expected earnings come from the pilot's own runs and not from a server's (ET-516), so the panel can say so.</summary>
public enum OwnRunsReason
{
    /// <summary>Nothing to say: no server is coupled, so there is no shared figure to miss.</summary>
    None,

    /// <summary>The pilot did not opt in to sharing with a coupled server; reading is part of the exchange.</summary>
    NotSharing,

    /// <summary>Fewer than the server's minimum of pilots contribute to the activity.</summary>
    TooFewPilots,

    /// <summary>Enough pilots, but not enough of their runs in the window to carry a figure.</summary>
    TooFewSharedRuns,

    /// <summary>The server could not be asked right now: not connected, refused or unreachable.</summary>
    ServerUnavailable
}
