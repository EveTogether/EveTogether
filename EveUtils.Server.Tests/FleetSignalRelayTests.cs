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
using EveUtils.Shared.Modules.Fleet.Commands;
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
/// ET-381: a fleet change made through the gRPC service reaches the fleet's other connected members because the
/// command that made it published its signal and <see cref="FleetChangeAnnouncer"/> relayed it — not because the gRPC
/// method remembered to announce. These two were the gaps: no gRPC method announced a new wing, and an accepted join
/// request only reached the requester by mail.
/// </summary>
public sealed class FleetSignalRelayTests : IDisposable
{
    private const int Commander = 90250177;
    private const int Wingman = 90000002;
    private const int Requester = 90000003;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly RecordingWriter _wingmanStream = new();
    private FleetChangeAnnouncer? _announcer;

    public FleetSignalRelayTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));
        services.AddFleetModule();
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(_factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(_factory);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        _announcer?.Dispose();
        _scope.Dispose();
        _provider.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ANewWing_ReachesAnotherConnectedMember()
    {
        var (service, context) = await _ServiceAsync();
        var fleetId = await _FleetWithWingmanAsync(service, context, FleetVisibility.Public);

        var reply = await service.CreateWing(new CreateWingRequest { FleetId = fleetId, Name = "Wing 2" }, context);

        Assert.True(reply.Accepted, reply.Message);
        Assert.Equal(FleetChangeKind.StructureChanged, _ChangeFor(fleetId));
    }

    [Fact]
    public async Task AnAcceptedJoinRequest_ReachesAnotherConnectedMember()
    {
        var (service, context) = await _ServiceAsync();
        var fleetId = await _FleetWithWingmanAsync(service, context, FleetVisibility.InviteOnly);
        var requested = await _Dispatcher().Send(new RequestToJoinCommand(fleetId, Requester), TestContext.Current.CancellationToken);
        Assert.True(requested.IsSuccess);
        _wingmanStream.Written.Clear();

        var requestId = requested.Value?.RequestId ?? throw new InvalidOperationException("The request carried no id.");
        var reply = await service.RespondToJoinRequest(new RespondToJoinRequestRequest { RequestId = requestId, Accept = true }, context);

        Assert.True(reply.Accepted, reply.Message);
        Assert.Equal(FleetChangeKind.RosterChanged, _ChangeFor(fleetId));
    }

    private FleetChangeKind _ChangeFor(long fleetId)
    {
        var received = Assert.Single(_wingmanStream.Written, w => w.Event.FleetId == fleetId).Event;
        Assert.Equal("fleet.changed", received.EventType);
        var change = JsonSerializer.Deserialize<FleetChangePayload>(received.PayloadJson)
            ?? throw new InvalidOperationException("The push carried no payload.");
        return change.Kind;
    }

    /// <summary>A fleet the commander created through the service, with the wingman on its roster. Seated straight
    /// into the store: how they got there is not what these tests are about.</summary>
    private async Task<long> _FleetWithWingmanAsync(FleetsGrpcService service, ServerCallContext context, FleetVisibility visibility)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await service.CreateFleet(new CreateFleetRequest { Name = "Roam", Visibility = (int)visibility }, context);
        Assert.True(created.Accepted, created.Message);
        await _scope.ServiceProvider.GetRequiredService<IFleetRepository>().AddMemberAsync(new FleetMember
        {
            FleetId = created.FleetId, CharacterId = Wingman, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1
        }, cancellationToken);
        _wingmanStream.Written.Clear();
        return created.FleetId;
    }

    private IDispatcher _Dispatcher() => _scope.ServiceProvider.GetRequiredService<IDispatcher>();

    private async Task<(FleetsGrpcService Service, ServerCallContext Context)> _ServiceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var authRepository = new ServerAuthRepository(_factory);
        var character = await authRepository.UpsertSyncedAsync(Commander, "Commander", new EncryptedToken([1], [2], [3]), null, cancellationToken);
        var sessions = new ServerSessionService(authRepository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, cancellationToken);

        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("commander", Commander, "Commander", new RecordingWriter()));
        clients.Add(new ConnectedClient("wingman", Wingman, "Wingman", _wingmanStream));

        var services = _scope.ServiceProvider;
        _announcer = new FleetChangeAnnouncer(services.GetRequiredService<IEventBus>(),
            services.GetRequiredService<IServiceScopeFactory>(), clients, NullLogger<FleetChangeAnnouncer>.Instance);
        await _announcer.StartAsync(cancellationToken);
        var service = new FleetsGrpcService(
            sessions, _Dispatcher(), clients, services.GetRequiredService<IFleetRepository>(),
            services.GetRequiredService<IFleetCompositionRepository>(),
            services.GetRequiredService<FleetCompositionAuthorizer>(),
            new FleetActivityTracker(services),
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
