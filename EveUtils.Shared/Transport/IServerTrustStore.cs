namespace EveUtils.Shared.Transport;

/// <summary>TOFU pin store: server address → pinned TLS cert fingerprint.</summary>
public interface IServerTrustStore
{
    string? GetFingerprint(string serverAddress);

    void Pin(string serverAddress, string fingerprint);
}
