using System.Globalization;
using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Transport;
using Grpc.Core;
using RunsGrpc = EveUtils.Grpc.Runs;

namespace EveUtils.Client.Transport;

public sealed class ServerRunSyncClient(
    GrpcChannelFactory channelFactory, IClientSessionStore sessionStore, ServerSessionRefresher refresher) : IServerRunSyncClient, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };

    public async Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
        string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default)
    {
        try
        {
            RunActionReply reply = await _InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
                client.PushRunAsync(new PushRunRequest { PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions) }, headers,
                    cancellationToken: cancellationToken), cancellationToken);
            DateTime? pushedAtUtc = DateTime.TryParse(reply.LastPushedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : null;
            return (reply.Accepted, reply.Message, pushedAtUtc);
        }
        catch (RpcException exception)
        {
            return (false, $"Run sync failed: {exception.Status.Detail}", null);
        }
        catch (InvalidOperationException exception)
        {
            return (false, $"Run sync failed: {exception.Message}", null);
        }
    }

    public async Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
        string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PullRunsReply reply = await _InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
            {
                var request = new PullRunsRequest { SinceUtc = sinceUtc.ToString("O") };
                request.GroupCodes.AddRange(groupCodes);
                return client.PullRunsAsync(request, headers, cancellationToken: cancellationToken);
            }, cancellationToken);
            IReadOnlyList<RunWirePayload> runs = reply.PayloadJson
                .Select(payloadJson => JsonSerializer.Deserialize<RunWirePayload>(payloadJson, SerializerOptions))
                .Where(payload => payload is not null)
                .Cast<RunWirePayload>()
                .ToList();
            return (reply.Accepted, reply.Message, runs);
        }
        catch (RpcException exception)
        {
            return (false, $"Run sync failed: {exception.Status.Detail}", []);
        }
        catch (InvalidOperationException exception)
        {
            return (false, $"Run sync failed: {exception.Message}", []);
        }
    }

    private async Task<TReply> _InvokeAsync<TReply>(string serverAddress, long actingCharacterId,
        Func<RunsGrpc.RunsClient, Metadata, AsyncUnaryCall<TReply>> rpc, CancellationToken cancellationToken)
    {
        if (actingCharacterId is < int.MinValue or > int.MaxValue)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The character id cannot be paired with a server session."));

        int sessionCharacterId = (int)actingCharacterId;
        ClientSessionTokens? session = await sessionStore.LoadForCharacterAsync(serverAddress, sessionCharacterId, cancellationToken);
        if (session is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated — pair with the server first."));

        var client = new RunsGrpc.RunsClient(channelFactory.CreatePinned(serverAddress));
        try
        {
            return await rpc(client, _BearerHeaders(session.AccessToken));
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unauthenticated)
        {
            ClientSessionTokens? refreshed = await refresher.RefreshAsync(serverAddress, sessionCharacterId, cancellationToken);
            if (refreshed is null)
                throw;
            return await rpc(client, _BearerHeaders(refreshed.AccessToken));
        }
    }

    private static Metadata _BearerHeaders(string accessToken) => new() { { "authorization", $"Bearer {accessToken}" } };
}
