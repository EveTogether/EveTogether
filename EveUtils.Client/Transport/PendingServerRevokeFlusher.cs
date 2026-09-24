using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Transport;

/// <summary>
/// Repeats the decouples a server missed. Called whenever a connection to that server comes up, which is the moment
/// it is known to be reachable again. A revoke the server still cannot be told about stays queued; one it answers —
/// even with "no such session" — is finished.
/// </summary>
public sealed class PendingServerRevokeFlusher(
    IPendingServerRevokeStore pendingRevokes,
    IServerSessionRevoker sessionRevoker,
    ILogger<PendingServerRevokeFlusher> logger) : ISingletonService
{
    public async Task FlushAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var pending in await pendingRevokes.ListAsync(serverAddress, cancellationToken))
            {
                var outcome = await sessionRevoker.RevokeAsync(serverAddress, pending.AccessToken, cancellationToken);
                if (outcome == ServerRevokeOutcome.Unreachable)
                    return;

                await pendingRevokes.RemoveAsync(pending.Id, cancellationToken);
                logger.LogInformation(
                    "Told {Server} about the earlier decouple of character {Character} ({Outcome}).",
                    serverAddress, pending.CharacterId, outcome);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not repeat the pending decouples for {Server}; they stay queued.", serverAddress);
        }
    }
}
