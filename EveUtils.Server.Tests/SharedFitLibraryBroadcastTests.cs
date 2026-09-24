using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Runtime;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-20: a shared fit that was stored or deleted was never announced, so every other member kept a stale library until
/// they reloaded. Since ET-383 the store and the delete are commands whose signal <see cref="SharedFitChangeRelay"/>
/// pushes as <c>fittings.shared</c> / <c>fittings.deleted</c> to every connected client, the acting one included.
/// </summary>
public sealed class SharedFitLibraryBroadcastTests : IDisposable
{
    private const int Jithran = 90250177;
    private const int Raymond = 90000002;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly RecordingWriter _jithran = new();
    private readonly RecordingWriter _raymond = new();
    private SharedFitChangeRelay? _relay;

    public SharedFitLibraryBroadcastTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        _relay?.Dispose();
        _scope.Dispose();
        _provider.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task DeleteSharedFit_WithTwoConnectedClients_TellsTheOtherClientWhichFitWentAway()
    {
        int fitId = await _SeedFitAsync();
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync();

        DeleteSharedFitReply reply = await service.DeleteSharedFit(new DeleteSharedFitRequest { Id = fitId }, _Context(accessToken));

        Assert.True(reply.Accepted, reply.Message);
        EventEnvelope announced = Assert.Single(_raymond.Written).Event;
        Assert.Equal("fittings.deleted", announced.EventType);
        Assert.Equal(fitId, JsonSerializer.Deserialize<FitDeletedPayload>(announced.PayloadJson)?.ServerFitId);
        Assert.Single(_jithran.Written);
    }

    [Fact]
    public async Task DeleteSharedFit_FitNotOnTheServer_TellsNobody()
    {
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync();

        DeleteSharedFitReply reply = await service.DeleteSharedFit(new DeleteSharedFitRequest { Id = 42 }, _Context(accessToken));

        Assert.False(reply.Accepted);
        Assert.Empty(_raymond.Written);
        Assert.Empty(_jithran.Written);
    }

    [Fact]
    public async Task ShareFit_NewFit_IsStoredAndTellsEveryClientWhoSharedIt()
    {
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync();

        ShareFitReply reply = await service.ShareFit(_Share("Guardian"), _Context(accessToken));

        Assert.True(reply.Accepted, reply.Message);
        EventEnvelope announced = Assert.Single(_raymond.Written).Event;
        Assert.Equal("fittings.shared", announced.EventType);
        Assert.Equal(Jithran, announced.CharacterId);
        Assert.Equal("Guardian", JsonSerializer.Deserialize<FitSharedPayload>(announced.PayloadJson)?.Name);
        Assert.Single(_jithran.Written);
        Assert.Single(await _scope.ServiceProvider.GetRequiredService<ISharedFitReader>().ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShareFit_SameFitAgain_NamesTheMatchAndTellsNobody()
    {
        (FittingsGrpcService service, string accessToken) = await _ServiceAsync();
        await service.ShareFit(_Share("Guardian"), _Context(accessToken));
        _raymond.Written.Clear();
        _jithran.Written.Clear();

        ShareFitReply reply = await service.ShareFit(_Share("Guardian copy"), _Context(accessToken));

        Assert.True(reply.Accepted);
        Assert.Contains("'Guardian'", reply.Message);
        Assert.Empty(_raymond.Written);
        Assert.Empty(_jithran.Written);
    }

    private async Task<int> _SeedFitAsync()
    {
        await using var db = ((IDbContextFactory<ServerDbContext>)_factory).CreateDbContext();
        var fit = new SharedFit
        {
            EsiFittingId = 1, Name = "Fit", ShipTypeId = 11987, SharedByCharacterId = Jithran, SharedByCharacterName = "Jithran",
            SharedAt = DateTimeOffset.UtcNow
        };
        db.Add(fit);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return fit.Id;
    }

    private async Task<(FittingsGrpcService Service, string AccessToken)> _ServiceAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var authRepository = new ServerAuthRepository(_factory);
        var character = await authRepository.UpsertSyncedAsync(Jithran, "Jithran", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(authRepository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);

        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("jithran", Jithran, "Jithran", _jithran));
        clients.Add(new ConnectedClient("raymond", Raymond, "Raymond", _raymond));

        var services = _scope.ServiceProvider;
        _relay = new SharedFitChangeRelay(services.GetRequiredService<IEventBus>(), services.GetRequiredService<IServiceScopeFactory>(),
            clients, NullLogger<SharedFitChangeRelay>.Instance);
        await _relay.StartAsync(cancellationToken);
        var service = new FittingsGrpcService(sessions, services.GetRequiredService<ISharedFitReader>(),
            services.GetRequiredService<IDispatcher>(), new AllowAllPolicy(), new FixedPrincipalAccessor(),
            NullLogger<FittingsGrpcService>.Instance);
        return (service, issued.AccessToken);
    }

    private static ShareFitRequest _Share(string name) => new()
    {
        EsiFittingId = 7, Name = name, ShipTypeId = 11987,
        RawJson = """{"ship_type_id":11987,"items":[{"type_id":3082,"quantity":1,"flag":"MedSlot0"}]}"""
    };

    private static ServerCallContext _Context(string bearer) =>
        new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {bearer}" } });

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
