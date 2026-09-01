namespace EveUtils.Client.Messaging;

/// <summary>Live state of the client's remote event-bus connection to a server.</summary>
public enum ServerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    /// <summary>Session expired/revoked — auto-reconnect stopped; the user must re-pair.</summary>
    SessionExpired,
    /// <summary>The server's TLS certificate no longer matches the pinned one — auto-reconnect stopped; the user must
    /// check the new fingerprint and re-pair.</summary>
    CertificateRejected
}
