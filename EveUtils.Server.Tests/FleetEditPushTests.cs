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
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
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
/// ET-360: an edit of a fleet is pushed as <c>fleet.changed</c> — to every connected character when the fleet was
/// public before or after the edit (so a non-member's row goes stale or vanishes), otherwise to its members only.
/// </summary>
public sealed class FleetEditPushTests : IDisposable
{
    private const int Creator = 90250177;
    private const int Onlooker = 90000002;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly RecordingWriter _creatorStream = new();
    private readonly RecordingWriter _onlookerStream = new();

    public FleetEditPushTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddFleetModule();
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _factory.Dispose();
    }

    /// <summary>AC-1: the fleet leaves discovery with this edit, and the non-member's row has to go with it.</summary>
    [Fact]
    public async Task APublicFleetMadeInviteOnly_ReachesAConnectedNonMember()
    {
        var (service, context) = await _ServiceAsync();
        var fleetId = await _CreateActiveFleetAsync(service, context, FleetVisibility.Public);

        var reply = await _EditAsync(service, context, fleetId, FleetVisibility.InviteOnly);

        Assert.True(reply.Accepted, reply.Message);
        var received = Assert.Single(_onlookerStream.Written).Event;
        Assert.Equal("fleet.changed", received.EventType);
        Assert.Equal(fleetId, received.FleetId);
        Assert.Equal(FleetChangeKind.Edited, JsonSerializer.Deserialize<FleetChangePayload>(received.PayloadJson)!.Kind);
    }

    /// <summary>AC-2: an invite-only fleet stays hidden from discovery, so its edit may not announce it to strangers.</summary>
    [Fact]
    public async Task AnInviteOnlyFleetEdited_StaysOnItsRoster()
    {
        var (service, context) = await _ServiceAsync();
        var fleetId = await _CreateActiveFleetAsync(service, context, FleetVisibility.InviteOnly);

        var reply = await _EditAsync(service, context, fleetId, FleetVisibility.InviteOnly);

        Assert.True(reply.Accepted, reply.Message);
        Assert.Empty(_onlookerStream.Written);
        Assert.Single(_creatorStream.Written, w => w.Event.FleetId == fleetId);
    }

    private async Task<long> _CreateActiveFleetAsync(FleetsGrpcService service, ServerCallContext context, FleetVisibility visibility)
    {
        var created = await service.CreateFleet(
            new CreateFleetRequest { Name = "Roam", Visibility = (int)visibility }, context);
        Assert.True(created.Accepted, created.Message);
        var fleets = _scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var fleet = await fleets.GetAsync(created.FleetId, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Fleet was not stored.");
        fleet.State = FleetState.Active;
        await fleets.UpdateAsync(fleet, TestContext.Current.CancellationToken);
        _creatorStream.Written.Clear();
        _onlookerStream.Written.Clear();
        return created.FleetId;
    }

    private static Task<FleetActionReply> _EditAsync(
        FleetsGrpcService service, ServerCallContext context, long fleetId, FleetVisibility visibility) =>
        service.EditFleet(new EditFleetRequest { FleetId = fleetId, Name = "Roam (renamed)", Visibility = (int)visibility }, context);

    private async Task<(FleetsGrpcService Service, ServerCallContext Context)> _ServiceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authRepository = new ServerAuthRepository(_factory);
        var character = await authRepository.UpsertSyncedAsync(Creator, "Creator", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(authRepository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);

        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("creator", Creator, "Creator", _creatorStream));
        clients.Add(new ConnectedClient("onlooker", Onlooker, "Onlooker", _onlookerStream));

        var services = _scope.ServiceProvider;
        var fleets = services.GetRequiredService<IFleetRepository>();
        var service = new FleetsGrpcService(
            sessions, services.GetRequiredService<IDispatcher>(), clients, new FleetChangeAnnouncer(fleets, clients), fleets,
            services.GetRequiredService<IFleetCompositionRepository>(),
            services.GetRequiredService<FleetCompositionAuthorizer>(),
            authRepository);
        return (service, new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {issued.AccessToken}" } }));
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
