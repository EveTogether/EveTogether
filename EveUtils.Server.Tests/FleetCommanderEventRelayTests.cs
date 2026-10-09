using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Runtime;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>ET-494: a commander-only fleet event reaches the fleet carrying the sender the server attached, never a
/// claim, so a member can neither pass as the commander nor arrive without a sender.</summary>
public sealed class FleetCommanderEventRelayTests
{
    private const int Commander = 95120001;
    private const int Member = 95120002;

    [Theory]
    [InlineData("abyssal", 0, true)]
    [InlineData("abyssal", Commander, false)]
    [InlineData("rooms", 0, true)]
    [InlineData("rooms", Commander, false)]
    [InlineData("room-proposed", 0, true)]
    public async Task MembersCommanderOnlyEvent_ReachesTheFleetOnlyUnderTheirOwnId(string kind, int claimed, bool relayed)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new SqliteServerDbContextFactory();
        var clients = new ConnectedClients();
        using ServiceProvider services = _Services(factory, clients);
        IFleetRepository repository = services.GetRequiredService<IFleetRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fleetId = await repository.AddAsync(new FleetEntity
        {
            Name = "Pocket", CreatorCharacterId = Commander, State = FleetState.Active,
            Activation = FleetActivation.Active, ActivatedAt = now, CreatedAt = now, LastActivityAt = now,
        }, cancellationToken);
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId, CharacterId = Commander, Role = FleetRole.FleetCommander, JoinTime = now,
        }, cancellationToken);
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId, CharacterId = Member, Role = FleetRole.SquadMember, JoinTime = now,
        }, cancellationToken);

        EventEnvelope envelope = WireEnvelopeFactory.ToEnvelope(_Event(kind, fleetId));
        envelope.CharacterId = claimed;
        var registry = new EventTypeRegistry();
        new FleetWireEvents().RegisterInto(registry);
        var commanderWriter = new RecordingWriter();
        clients.Add(new ConnectedClient("commander", Commander, "Commander", commanderWriter));
        var repositoryAuth = new ServerAuthRepository(factory);
        var character = await repositoryAuth.UpsertSyncedAsync(Member, "Member", new EncryptedToken([1], [2], [3]), null,
            cancellationToken);
        var sessions = new ServerSessionService(repositoryAuth, NullLogger<ServerSessionService>.Instance);
        string accessToken = (await sessions.IssueAsync(character.Id, cancellationToken)).AccessToken;
        var service = new EventBusStreamService(sessions, registry, clients, new FleetPresenceSourceGuard(), services);

        await service.Attach(new SingleMessageReader(new ClientEnvelope { Event = envelope }), new RecordingWriter(),
            new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {accessToken}" } }));

        ServerEnvelope[] arrived = [.. commanderWriter.Written.Where(message => message.Event.EventType == envelope.EventType)];
        Assert.Equal(relayed ? 1 : 0, arrived.Length);
        Assert.All(arrived, message => Assert.Equal(Member, message.Event.CharacterId));
    }

    private static IIntegrationEvent _Event(string kind, long fleetId) => kind switch
    {
        "rooms" => new FleetRunGroupRoomsEvent(new RunGroupRooms(fleetId, "AB-ROOMS", [new RunGroupRoom(DateTime.UtcNow, null)])),
        "room-proposed" => new FleetRunGroupRoomProposedEvent(
            new RunGroupRoomProposal(fleetId, "AB-ROOMS", DateTime.UtcNow, RoomCertainty.Sure)),
        _ => new FleetRunGroupAbyssalUpdatedEvent(
            new RunGroupAbyssalUpdate(fleetId, ActivityKind.Abyssal, "AB-ROOMS", 3, "Dark"))
    };

    private static ServiceProvider _Services(SqliteServerDbContextFactory factory, ConnectedClients clients)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServerIdentity();
        services.AddPermissionRegistry();
        services.AddCqrs();
        services.AddEventBus();
        services.AddSharedServices(ExecutionHost.Server);
        services.AddAutoServices(typeof(EventBusStreamService).Assembly, ExecutionHost.Server);
        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));
        services.AddFleetModule();
        services.AddMessagingModule();
        services.AddSingleton<IDbContextFactory<SharedDbContext>>(factory);
        services.AddSingleton<IDbContextFactory<ServerDbContext>>(factory);
        services.AddSingleton(clients);
        services.AddScoped<FleetBroadcastResolver>();
        services.AddSingleton<IAccessPolicy>(new AllowAll());
        services.AddSingleton<IPrincipalAccessor>(new FixedPrincipalAccessor());
        return services.BuildServiceProvider();
    }

    private sealed class AllowAll : IAccessPolicy
    {
        public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FixedPrincipalAccessor : IPrincipalAccessor
    {
        public Principal Current { get; } = new("fleet-test", null);
    }

    private sealed class SingleMessageReader(ClientEnvelope message) : IAsyncStreamReader<ClientEnvelope>
    {
        private bool _wasRead;

        public ClientEnvelope Current => message;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            bool first = !_wasRead;
            _wasRead = true;
            return Task.FromResult(first);
        }
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
