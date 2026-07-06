namespace EveUtils.Client.LocalApi;

/// <summary>Lifecycle state of the opt-in local widget API host, surfaced in the Settings dialog.</summary>
public enum LocalApiStatus
{
    /// <summary>Disabled or not started.</summary>
    Stopped,

    /// <summary>Listening on the loopback interface.</summary>
    Running,

    /// <summary>Enabled but the configured port was already taken — left stopped, not a crash.</summary>
    PortInUse,

    /// <summary>Enabled but the host failed to start for another reason (see the message).</summary>
    Error
}
