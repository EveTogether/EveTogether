using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using Grpc.Core;
using GrpcSession = EveUtils.Grpc.Session;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Transport;

/// <summary>
/// Calls the server's <c>Session.Revoke</c> RPC over the TOFU-pinned channel to decouple a
/// character: the server invalidates the session and cuts the bus stream. The client still drops its
/// local session afterwards (so an unreachable server can't keep us "coupled").
/// </summary>
public sealed class ServerSessionClient(GrpcChannelFactory channelFactory) : ISingletonService
{
    public async Task<(bool Ok, string Message)> RevokeAsync(
        string serverAddress, string sessionToken, CancellationToken cancellationToken = default)
    {
        var channel = channelFactory.CreatePinned(serverAddress);
        var client = new GrpcSession.SessionClient(channel);
        try
        {
            // A short deadline so decoupling a stale/unreachable server fails fast instead of hanging on the TCP
            // connect timeout — the local session is dropped by the caller regardless of the server's reply.
            var reply = await client.RevokeAsync(
                new RevokeRequest { SessionToken = sessionToken },
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            return (reply.Ok, reply.Message);
        }
        catch (RpcException ex)
        {
            return (false, ex.Status.Detail);
        }
    }
}
