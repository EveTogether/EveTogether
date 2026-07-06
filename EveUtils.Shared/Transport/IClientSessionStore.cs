namespace EveUtils.Shared.Transport;

/// <summary>
/// Stores server session tokens per (server address, character). Multiple characters can be paired to
/// the same server, so sessions are keyed by character id — pairing a second
/// character no longer overwrites the first.
/// </summary>
public interface IClientSessionStore
{
    Task SaveAsync(string serverAddress, ClientSessionTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>The most recently saved session for the server (used to attach the event bus).</summary>
    Task<ClientSessionTokens?> LoadAsync(string serverAddress, CancellationToken cancellationToken = default);

    /// <summary>The session for a specific character on the server (used right after (re)pairing).</summary>
    Task<ClientSessionTokens?> LoadForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default);

    /// <summary>All sessions for the server — one per paired character (for synced status).</summary>
    Task<IReadOnlyList<ClientSessionTokens>> LoadAllAsync(string serverAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes a stale/expired session for a character so the bus stops picking it.</summary>
    Task RemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default);

    /// <summary>Distinct server addresses with at least one stored session — used to reconnect every coupled server on startup.</summary>
    Task<IReadOnlyList<string>> ListServersAsync(CancellationToken cancellationToken = default);

    /// <summary>Server addresses this character is coupled to (has a session for) — for the per-character server list.</summary>
    Task<IReadOnlyList<string>> ListServersForCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}
