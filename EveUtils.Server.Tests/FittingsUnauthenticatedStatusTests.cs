using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-85: a refused fit-sharing RPC answers with <see cref="StatusCode.Unauthenticated"/> — the same signal
/// <c>EventBusStreamService.Attach</c> and the Fleets RPCs (ET-78) already raise — instead of an <c>Accepted/Ok =
/// false</c> reply payload. That status is what the client's <c>ServerFitShareClient.InvokeAsync</c> watches for to
/// refresh the session and retry the call in flight; a reply payload sailed straight past it, leaving the call lost
/// until the next 30s heartbeat.
/// </summary>
public class FittingsUnauthenticatedStatusTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task AMissingBearer_IsRefusedWithUnauthenticated_NotAnAcceptedFalseReply()
    {
        var service = Service();

        var thrown = await Assert.ThrowsAsync<RpcException>(
            () => service.ShareFit(new ShareFitRequest { Name = "Roam Ship" }, Context(bearer: null)));

        Assert.Equal(StatusCode.Unauthenticated, thrown.StatusCode);
        // The server's own words, unchanged — the client surfaces Status.Detail where it used to surface reply.Message.
        Assert.Equal("Not authenticated — pair with the server first.", thrown.Status.Detail);
    }

    [Fact]
    public async Task AnUnknownToken_IsRefusedTheSameWayOnAListRead_NotAsAnEmptyOkReply()
    {
        var service = Service();

        var thrown = await Assert.ThrowsAsync<RpcException>(
            () => service.GetSharedFits(new GetSharedFitsRequest(), Context(bearer: "no-such-token")));

        Assert.Equal(StatusCode.Unauthenticated, thrown.StatusCode);
    }

    /// <summary>Only the session service is reached before the gate refuses, so the rest of the graph stays out of it.</summary>
    private FittingsGrpcService Service() =>
        new(new ServerSessionService(new ServerAuthRepository(_factory), NullLogger<ServerSessionService>.Instance),
            null!, null!, null!, null!, null!);

    private static ServerCallContext Context(string? bearer)
    {
        var headers = new Metadata();
        if (bearer is not null)
            headers.Add("authorization", $"Bearer {bearer}");
        return new HeadersOnlyCallContext(headers);
    }

    /// <summary>Only the request headers are read before the gate refuses, so the rest of the context is left
    /// unimplemented rather than pulling in Grpc.Core.Testing for one factory call.</summary>
    private sealed class HeadersOnlyCallContext(Metadata headers) : ServerCallContext
    {
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override string MethodCore => "test";
        protected override string HostCore => "test";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => throw new NotSupportedException();
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
