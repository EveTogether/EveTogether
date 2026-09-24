using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-20: deleting a shared fit was never announced, so every other member kept showing it until they reloaded. The
/// server now pushes <c>fittings.deleted</c> with the removed fit's id to every connected client.
/// </summary>
public sealed class SharedFitDeleteBroadcastTests
{
    private const int Jithran = 90250177;
    private const int Raymond = 90000002;
    private const int SharedFitId = 42;

    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task DeleteSharedFit_WithTwoConnectedClients_TellsTheOtherClientWhichFitWentAway()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RecordingWriter jithran = new(), raymond = new();
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync(removed: true, jithran, raymond, cancellationToken);

        DeleteSharedFitReply reply = await service.DeleteSharedFit(new DeleteSharedFitRequest { Id = SharedFitId }, _Context(accessToken));

        Assert.True(reply.Accepted, reply.Message);
        EventEnvelope announced = Assert.Single(raymond.Written).Event;
        Assert.Equal("fittings.deleted", announced.EventType);
        Assert.Equal(SharedFitId, JsonSerializer.Deserialize<FitDeletedPayload>(announced.PayloadJson)?.ServerFitId);
    }

    [Fact]
    public async Task DeleteSharedFit_FitNotOnTheServer_TellsNobody()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RecordingWriter jithran = new(), raymond = new();
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync(removed: false, jithran, raymond, cancellationToken);

        DeleteSharedFitReply reply = await service.DeleteSharedFit(new DeleteSharedFitRequest { Id = SharedFitId }, _Context(accessToken));

        Assert.False(reply.Accepted);
        Assert.Empty(raymond.Written);
    }

    private async Task<(FittingsGrpcService Service, string AccessToken)> _ServiceAsync(
        bool removed, RecordingWriter jithran, RecordingWriter raymond, CancellationToken cancellationToken)
    {
        var authRepository = new ServerAuthRepository(_factory);
        var character = await authRepository.UpsertSyncedAsync(Jithran, "Jithran", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(authRepository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);

        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("jithran", Jithran, "Jithran", jithran));
        clients.Add(new ConnectedClient("raymond", Raymond, "Raymond", raymond));

        var service = new FittingsGrpcService(sessions, new StubSharedFitRepository(removed), new AllowAllPolicy(),
            new FixedPrincipalAccessor(), clients, NullLogger<FittingsGrpcService>.Instance);
        return (service, issued.AccessToken);
    }

    private static ServerCallContext _Context(string bearer) =>
        new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {bearer}" } });

    private sealed class StubSharedFitRepository(bool removed) : ISharedFitRepository
    {
        public Task<bool> RemoveAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(removed);

        public Task AddAsync(SharedFit fit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedFit?> AddOrMatchAsync(SharedFit fit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedFit?> GetAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SharedFit>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task BackfillContentHashesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AllowAllPolicy : IAccessPolicy
    {
        public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FixedPrincipalAccessor : IPrincipalAccessor
    {
        public Principal Current { get; } = new("owner", null);
    }

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        public List<ServerEnvelope> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

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
