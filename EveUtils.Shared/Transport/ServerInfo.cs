namespace EveUtils.Shared.Transport;

/// <summary>
/// Locally remembered metadata for a coupled server. The display name prefers the user-given
/// <see cref="Label"/>, then the server's own configured <see cref="ServerName"/> (from pairing), and
/// only falls back to the raw address.
/// </summary>
public sealed record ServerInfo(string? Label, string? ServerName)
{
    public string DisplayName(string serverAddress) =>
        !string.IsNullOrWhiteSpace(Label) ? Label!
        : !string.IsNullOrWhiteSpace(ServerName) ? ServerName!
        : serverAddress;
}
