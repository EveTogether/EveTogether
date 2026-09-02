using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using GrpcSession = EveUtils.Grpc.Session;

namespace EveUtils.Client.Transport;

/// <summary>What the server answered to a <c>Session.Refresh</c>. <see cref="Ok"/> false means the server was
/// reached and refused the token; a server that could not be reached throws instead, so the two are never
/// confused — that distinction is the whole reason ET-121's rules are worth testing.</summary>
/// <param name="SessionGone">Set only on a refusal, and only when the server said so outright: the session it
/// names is not there any more, so no retry can bring it back and the user has to couple again. False covers both
/// "the token is merely stale" and a server too old to distinguish them, which keeps ET-121's retry as the
/// default — nobody is sent to re-pair on a guess.</param>
public readonly record struct ServerSessionRefreshReply(
    bool Ok, string AccessToken, string RefreshToken, int SessionId = 0, bool SessionGone = false);

/// <summary>
/// The <c>Session.Refresh</c> round-trip, behind a seam. <see cref="ServerSessionRefresher"/> owns rules that decide
/// whether a user keeps their pairing — one rotation per expiry, a rejection that changes nothing on disk — and those
/// rules are worth proving without standing up a TLS-pinned gRPC server to prove them against.
/// </summary>
public interface IServerSessionRefreshCall
{
    /// <summary>Sends the refresh token, naming the session it belongs to (0 when not known yet) so a refusal can
    /// say which kind it is. Throws when the server could not be reached.</summary>
    Task<ServerSessionRefreshReply> RefreshAsync(
        string serverAddress, string refreshToken, int sessionId = 0, CancellationToken cancellationToken = default);
}

/// <summary>The real call, over the pinned channel.</summary>
public sealed class GrpcServerSessionRefreshCall(GrpcChannelFactory channelFactory)
    : IServerSessionRefreshCall, ISingletonService
{
    public async Task<ServerSessionRefreshReply> RefreshAsync(
        string serverAddress, string refreshToken, int sessionId = 0, CancellationToken cancellationToken = default)
    {
        var channel = channelFactory.CreatePinned(serverAddress);
        var client = new GrpcSession.SessionClient(channel);
        var reply = await client.RefreshAsync(
            new RefreshRequest { SessionRefreshToken = refreshToken, SessionId = sessionId },
            cancellationToken: cancellationToken);
        return new ServerSessionRefreshReply(
            reply.Ok, reply.SessionToken, reply.SessionRefreshToken, reply.SessionId,
            reply.Refusal == SessionRefusal.SessionGone);
    }
}
