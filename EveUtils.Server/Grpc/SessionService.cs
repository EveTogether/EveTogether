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
        var issued = await sessions.RefreshAsync(request.SessionRefreshToken, context.CancellationToken);
        return issued is null
            ? new SessionReply { Ok = false, Message = "Invalid or expired refresh token." }
            : new SessionReply { Ok = true, SessionToken = issued.AccessToken, SessionRefreshToken = issued.RefreshToken, Message = "ok" };
    }

    public override async Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        var session = await sessions.ValidateAsync(request.SessionToken, context.CancellationToken);
        if (session is null)
            return new HeartbeatReply { Ok = false };

        await sessions.TouchAsync(request.SessionToken, context.CancellationToken);
        return new HeartbeatReply { Ok = true };
    }

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
