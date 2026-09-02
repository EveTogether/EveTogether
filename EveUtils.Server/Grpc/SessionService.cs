using EveUtils.Grpc;
using EveUtils.Server.Auth;
using Grpc.Core;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Server session lifecycle: silent refresh on reconnect + a ~30s heartbeat that updates
/// presence for the admin panel. Both validate the server-issued session token (not the EVE
/// token).
/// </summary>
public sealed class SessionService(ServerSessionService sessions) : Session.SessionBase
{
    public override async Task<SessionReply> Refresh(RefreshRequest request, ServerCallContext context)
    {
        // The peer is the only thing in the request that says WHICH machine was refused — the request carries no
        // character id — and a refusal is exactly the moment you want to know that.
        var result = await sessions.RefreshAsync(
            request.SessionRefreshToken, context.Peer, request.SessionId, context.CancellationToken);

        if (result.Issued is not { } issued)
            return new SessionReply
            {
                Ok = false,
                // The reason travels as its own field rather than in the message, because the client acts on it:
                // it stops retrying only when the server says the session is gone (ET-123).
                Refusal = Map(result.Refusal),
                Message = result.Refusal == SessionRefusalReason.SessionGone
                    ? "This session no longer exists on the server — couple the character again."
                    : "Invalid or expired refresh token."
            };

        return new SessionReply
        {
            Ok = true,
            SessionToken = issued.AccessToken,
            SessionRefreshToken = issued.RefreshToken,
            SessionId = issued.SessionId,
            Message = "ok"
        };
    }

    public override async Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        var session = await sessions.ValidateAsync(request.SessionToken, context.CancellationToken);
        if (session is null)
            return new HeartbeatReply { Ok = false };

        await sessions.TouchAsync(request.SessionToken, context.CancellationToken);
        // Riding along on the tick a healthy client already makes: it is how a client paired before session ids
        // existed comes to know its own, without waiting for a rotation.
        return new HeartbeatReply { Ok = true, SessionId = session.Id };
    }

    private static SessionRefusal Map(SessionRefusalReason reason) => reason switch
    {
        SessionRefusalReason.SessionGone => SessionRefusal.SessionGone,
        SessionRefusalReason.Retry => SessionRefusal.Retry,
        _ => SessionRefusal.Unspecified
    };

    public override async Task<RevokeReply> Revoke(RevokeRequest request, ServerCallContext context)
    {
        var revoked = await sessions.RevokeAsync(request.SessionToken, context.CancellationToken);
        return new RevokeReply
        {
            Ok = revoked,
            Message = revoked ? "ok" : "No matching session."
        };
    }
}
