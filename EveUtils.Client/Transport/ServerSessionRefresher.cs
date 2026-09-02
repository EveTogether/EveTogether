using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Transport;

/// <summary>What a server-session refresh came to: the one place that classifies the outcome.</summary>
public enum ServerSessionRefreshOutcome
{
    /// <summary>The session was rotated (or another caller had just rotated it) and is usable again.</summary>
    Refreshed,

    /// <summary>The server answered and refused the refresh token. Says nothing about whether the pairing is
    /// gone — see the remarks on <see cref="ServerSessionRefresher"/>.</summary>
    Rejected,

    /// <summary>The server could not be reached, or there was nothing stored to refresh with.</summary>
    Unavailable
}

/// <summary>
/// The single owner of the server-session refresh. Every path that finds the 1-hour access token expired — the bus
/// connect loop, its backup heartbeat, and the unary clients (<see cref="FleetClient"/>,
/// <see cref="ServerFitShareClient"/>, <see cref="ServerRunSyncClient"/>) on a 401 — goes through here, so the
/// rotation is serialised across all of them.
/// <para>That gate is the point. A refresh ROTATES the refresh token, so two in flight for one (server, character)
/// leave the loser presenting a token the server has already replaced. <c>ServerConnection</c> used to guard its own
/// two callers with a private semaphore while this class guarded nothing, so the two halves could not see each other
/// — and the loser's rejection cost the user their pairing (ET-121).</para>
/// <para><b>A rejection is not proof that the pairing is gone.</b> The server refuses a refresh token it cannot find
/// in its table, and a token goes missing from that table for reasons that have nothing to do with the user: a
/// rotation this client never got to persist (the reply was lost, the machine suspended, the process was killed
/// between the round-trip and the save). The server's own refresh window is a sliding year, so "expired" is close to
/// theoretical. Callers must therefore treat <see cref="ServerSessionRefreshOutcome.Rejected"/> as a state to show,
/// never as a licence to delete stored credentials.</para>
/// </summary>
public sealed class ServerSessionRefresher(IServerSessionRefreshCall call, IClientSessionStore sessionStore) : ISingletonService
{
    // One gate per (server, character) — the same shape ClientTokenRefreshService uses for EVE's rotating refresh
    // tokens, and for the same reason. Keyed on the address the caller used: two spellings of one server resolve to
    // the same stored row, so the recheck inside the gate still catches them (it re-reads through the store, which
    // resolves by certificate fingerprint).
    private readonly ConcurrentDictionary<(string Server, int Character), SemaphoreSlim> _gates =
        new(ServerCharacterComparer.Instance);

    /// <summary>
    /// Refreshes the (server, character) session and persists the rotated tokens. <paramref name="staleAccessToken"/>
    /// is the access token whose rejection prompted this call: if the store already holds a different one by the time
    /// this caller gets the gate, someone else refreshed while it waited and the fresh pair is returned without a
    /// second round-trip — one rotation per expiry instead of one per caller.
    /// </summary>
    public async Task<(ServerSessionRefreshOutcome Outcome, ClientSessionTokens? Session)> TryRefreshAsync(
        string serverAddress, int characterId, string? staleAccessToken = null, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd((serverAddress, characterId), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadAsync(serverAddress, characterId, cancellationToken);
            if (session is null || string.IsNullOrEmpty(session.RefreshToken))
                return (ServerSessionRefreshOutcome.Unavailable, null); // nothing stored to refresh with

            // Someone else rotated while this caller queued — use their result rather than burning the fresh token.
            if (staleAccessToken is not null && session.AccessToken != staleAccessToken)
                return (ServerSessionRefreshOutcome.Refreshed, session);

            try
            {
                var reply = await call.RefreshAsync(serverAddress, session.RefreshToken, cancellationToken);
                if (!reply.Ok)
                    // Rejected, and the store is left exactly as it was. Deliberate: see the class remarks.
                    return (ServerSessionRefreshOutcome.Rejected, session);

                var rotated = new ClientSessionTokens(
                    reply.AccessToken, reply.RefreshToken, session.CharacterName, session.CharacterId);

                // Persist before returning: a caller that starts using the rotated access token while the old pair is
                // still the one on disk would leave the store holding a token the server has already replaced — the
                // exact stale-token state this class exists to prevent.
                await sessionStore.SaveAsync(serverAddress, rotated, cancellationToken);
                return (ServerSessionRefreshOutcome.Refreshed, rotated);
            }
            catch (Exception)
            {
                // Unreachable (network/TLS/cancellation) — keep the stored session and let the caller retry. On a real
                // shutdown the caller's own cancellation token ends its loop.
                return (ServerSessionRefreshOutcome.Unavailable, session);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Refreshes and returns the rotated session, or null when it could not be refreshed. The shape the
    /// unary clients want: they retry once on success and surface their original 401 otherwise.</summary>
    public async Task<ClientSessionTokens?> RefreshAsync(
        string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        var (outcome, session) = await TryRefreshAsync(serverAddress, characterId, cancellationToken: cancellationToken);
        return outcome == ServerSessionRefreshOutcome.Refreshed ? session : null;
    }

    // characterId 0 means "any session for this server" — a server-scoped call that is not acting as one character.
    private Task<ClientSessionTokens?> LoadAsync(string serverAddress, int characterId, CancellationToken cancellationToken) =>
        characterId != 0
            ? sessionStore.LoadForCharacterAsync(serverAddress, characterId, cancellationToken)
            : sessionStore.LoadAsync(serverAddress, cancellationToken);

    /// <summary>Server addresses compare case-insensitively (a host name is not case-sensitive), so two spellings
    /// that differ only in case take one gate rather than one each.</summary>
    private sealed class ServerCharacterComparer : IEqualityComparer<(string Server, int Character)>
    {
        public static readonly ServerCharacterComparer Instance = new();

        public bool Equals((string Server, int Character) x, (string Server, int Character) y) =>
            x.Character == y.Character && string.Equals(x.Server, y.Server, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Server, int Character) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Server), obj.Character);
    }
}
