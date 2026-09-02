namespace EveUtils.Client.Messaging;

/// <summary>Live state of the client's remote event-bus connection to a server.</summary>
public enum ServerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    /// <summary>The server refuses the stored session and will not renew it. The pairing is KEPT and retried on a
    /// slow cadence — this is a state to show, not a decision to unpair (ET-121). It clears by itself if the refresh
    /// starts working again; if it persists, re-pairing is the remedy.</summary>
    SessionExpired,
    /// <summary>The server says this session does not exist there any more — swept as abandoned (ET-123), revoked
    /// from the admin panel, or past its refresh window. Unlike <see cref="SessionExpired"/> this is final, so the
    /// retrying stops: the only way back is coupling the character again, and going on saying "retrying" would tell
    /// the user the app is busy at the very moment they are the ones who have to act.</summary>
    SessionGone,
    /// <summary>The server's TLS certificate no longer matches the pinned one — auto-reconnect stopped; the user must
    /// check the new fingerprint and re-pair.</summary>
    CertificateRejected
}
