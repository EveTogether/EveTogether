using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using GrpcSession = EveUtils.Grpc.Session;

namespace EveUtils.Client.Transport;

/// <summary>What the server answered to a <c>Session.Refresh</c>. <see cref="Ok"/> false means the server was
/// reached and refused the token; a server that could not be reached throws instead, so the two are never
/// confused — that distinction is the whole reason ET-121's rules are worth testing.</summary>
public readonly record struct ServerSessionRefreshReply(bool Ok, string AccessToken, string RefreshToken);

/// <summary>
/// The <c>Session.Refresh</c> round-trip, behind a seam. <see cref="ServerSessionRefresher"/> owns rules that decide
/// whether a user keeps their pairing — one rotation per expiry, a rejection that changes nothing on disk — and those
/// rules are worth proving without standing up a TLS-pinned gRPC server to prove them against.
/// </summary>
public interface IServerSessionRefreshCall
{
    /// <summary>Sends the refresh token. Throws when the server could not be reached.</summary>
    Task<ServerSessionRefreshReply> RefreshAsync(
        string serverAddress, string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>The real call, over the pinned channel.</summary>
public sealed class GrpcServerSessionRefreshCall(GrpcChannelFactory channelFactory)
    : IServerSessionRefreshCall, ISingletonService
{
    public async Task<ServerSessionRefreshReply> RefreshAsync(
        string serverAddress, string refreshToken, CancellationToken cancellationToken = default)
    {
        var channel = channelFactory.CreatePinned(serverAddress);
        var client = new GrpcSession.SessionClient(channel);
        var reply = await client.RefreshAsync(
            new RefreshRequest { SessionRefreshToken = refreshToken }, cancellationToken: cancellationToken);
        return new ServerSessionRefreshReply(reply.Ok, reply.SessionToken, reply.SessionRefreshToken);
    }
}
