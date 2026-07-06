using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using Grpc.Core;
using GrpcSession = EveUtils.Grpc.Session;

namespace EveUtils.Client.Transport;

/// <summary>
/// Refreshes a server session when a unary RPC is rejected with <c>Unauthenticated</c> because the
/// 1-hour access token expired (the 30-day refresh token is still valid). The event-bus stream refreshes itself only
/// on reconnect, so a stable open stream lets the stored access token silently expire — after which the unary clients
/// (<see cref="FleetClient"/>, <see cref="ServerFitShareClient"/>) would keep sending the expired token and fail
/// "Not authenticated" even though the stream still shows connected. They call this on a 401, then retry once.
/// </summary>
public sealed class ServerSessionRefresher(GrpcChannelFactory channelFactory, IClientSessionStore sessionStore) : ISingletonService
{
    /// <summary>Refreshes the (server, character) session via <c>Session.Refresh</c> and persists the rotated tokens.
    /// Returns the fresh session, or null when there's nothing to refresh / the server rejected it (re-pair needed)
    /// / the server was unreachable — in which case the caller surfaces the original failure.</summary>
    public async Task<ClientSessionTokens?> RefreshAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        var session = characterId != 0
            ? await sessionStore.LoadForCharacterAsync(serverAddress, characterId, cancellationToken)
            : await sessionStore.LoadAsync(serverAddress, cancellationToken);
        if (session is null || string.IsNullOrEmpty(session.RefreshToken))
            return null;

        try
        {
            var channel = channelFactory.CreatePinned(serverAddress);
            var client = new GrpcSession.SessionClient(channel);
            var reply = await client.RefreshAsync(
                new RefreshRequest { SessionRefreshToken = session.RefreshToken }, cancellationToken: cancellationToken);
            if (!reply.Ok)
                return null; // server reached us and refused → genuinely expired/invalid (re-pair)

            var rotated = new ClientSessionTokens(reply.SessionToken, reply.SessionRefreshToken, session.CharacterName, session.CharacterId);
            await sessionStore.SaveAsync(serverAddress, rotated, cancellationToken);
            return rotated;
        }
        catch (Exception)
        {
            return null; // unreachable/transient — keep the session, the caller surfaces the original 401
        }
    }
}
