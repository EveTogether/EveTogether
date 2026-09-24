using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Transport;

/// <summary>
/// Tears down a character↔server coupling: revoke the server session (so the bus stream is cut), drop the
/// local session, then either detach the bus from that server (no characters left) or re-attach it with a remaining
/// character's session. Shared by the per-character gear-button decouple and the per-server "decouple" action in the
/// Fleets window, so the teardown sequence lives in one place. A revoke the server never heard — it was unreachable —
/// is kept and repeated on the next connection to it (<see cref="PendingServerRevokeFlusher"/>).
/// </summary>
public sealed class ServerCouplingService(
    IClientSessionStore sessionStore,
    IServerSessionRevoker sessionRevoker,
    IPendingServerRevokeStore pendingRevokes,
    IRemoteBusConnector busConnector)
    : ISingletonService
{
    /// <summary>Decouples one character from one server, keeping any other characters coupled to it live. Returns
    /// what the server answered to the revoke — <see cref="ServerRevokeOutcome.Unreachable"/> means it is queued and
    /// repeated on the next connection.</summary>
    public async Task<ServerRevokeOutcome> DecoupleCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        var outcome = await RevokeAndRemoveAsync(serverAddress, characterId, cancellationToken);

        var remaining = await sessionStore.LoadAllAsync(serverAddress, cancellationToken);
        if (remaining.Count == 0)
            await busConnector.DetachAsync(serverAddress, cancellationToken);
        else
            await busConnector.AttachAsync(serverAddress, cancellationToken: cancellationToken);
        return outcome;
    }

    /// <summary>Decouples EVERY coupled character from one server and detaches its bus — fully removes it from the
    /// coupled-server list (e.g. a stale/unreachable server lingering in the Fleets window).</summary>
    public async Task DecoupleServerAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        foreach (var session in await sessionStore.LoadAllAsync(serverAddress, cancellationToken))
            await RevokeAndRemoveAsync(serverAddress, session.CharacterId, cancellationToken);

        await busConnector.DetachAsync(serverAddress, cancellationToken);
    }

    private async Task<ServerRevokeOutcome> RevokeAndRemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken)
    {
        var session = await sessionStore.LoadForCharacterAsync(serverAddress, characterId, cancellationToken);
        var outcome = session is null
            ? ServerRevokeOutcome.NoSuchSession
            : await sessionRevoker.RevokeAsync(serverAddress, session.AccessToken, cancellationToken);
        if (session is not null && outcome == ServerRevokeOutcome.Unreachable)
            await pendingRevokes.QueueAsync(serverAddress, characterId, session.AccessToken, cancellationToken);
        await sessionStore.RemoveAsync(serverAddress, characterId, cancellationToken);
        return outcome;
    }
}
