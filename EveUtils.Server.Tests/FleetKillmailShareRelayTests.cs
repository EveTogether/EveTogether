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
using EveUtils.Shared.Modules.Messaging;
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

public sealed class FleetKillmailShareRelayTests
{
    private const int Sender = 95110001;
    private const int FleetMate = 95110002;
    private const int Stranger = 95110099;

    [Theory]
    [InlineData(true, true, true, 1, 1)]
    [InlineData(true, true, false, 0, 0)]
    [InlineData(true, false, true, 0, 0)]
    [InlineData(false, true, true, 0, 0)]
    public async Task WireAndRelay_MembershipAndActivationScenario_RoundTripsAndReachesExpectedConnections(
        bool senderIsMember,
        bool active,
        bool permissionAllowed,
        int expectedSenderMessages,
        int expectedFleetMateMessages)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new SqliteServerDbContextFactory();
        var clients = new ConnectedClients();
        var policy = new FleetMetricsPolicy(permissionAllowed);
        using ServiceProvider services = BuildServices(factory, clients, policy);
        IFleetRepository repository = services.GetRequiredService<IFleetRepository>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long fleetId = await repository.AddAsync(new FleetEntity
        {
            Name = "Roam",
            CreatorCharacterId = Sender,
            State = FleetState.Active,
            Activation = active ? FleetActivation.Active : FleetActivation.Forming,
            ActivatedAt = active ? now : null,
            CreatedAt = now,
            LastActivityAt = now,
        }, cancellationToken);
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = FleetMate,
            Role = FleetRole.SquadMember,
            JoinTime = now,
        }, cancellationToken);
        int attachedCharacterId = senderIsMember ? Sender : Stranger;
        if (senderIsMember)
        {
            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId,
                CharacterId = Sender,
                Role = FleetRole.FleetCommander,
                JoinTime = now,
            }, cancellationToken);
        }

        var original = new FleetKillmailShareEvent(new FleetKillmailShare
        {
            FleetId = fleetId,
            UnixMs = 1791288000000,
            Killmails =
            [
                new FleetKillmailReference
                {
                    KillmailId = 138560925,
                    Hash = "2305db84acc094d6ecfeda59ffcb3b5f70b9eb3f",
                    KillmailTimeUtc = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc),
                    IsLoss = true,
                },
            ],
        }, Sender);
        EventEnvelope envelope = WireEnvelopeFactory.ToEnvelope(original);
        envelope.CharacterId = 0;
        var registry = new EventTypeRegistry();
        new FleetWireEvents().RegisterInto(registry);
        var local = new List<FleetKillmailShareEvent>();
        using IDisposable subscription = services.GetRequiredService<IEventBus>()
            .Subscribe<FleetKillmailShareEvent>((evt, _) =>
            {
                local.Add(evt);
                return Task.CompletedTask;
            });

        var senderWriter = new RecordingWriter();
        var fleetMateWriter = new RecordingWriter();
        clients.Add(new ConnectedClient("fleet-mate", FleetMate, "Fleet mate", fleetMateWriter));
        (ServerSessionService sessions, string accessToken) =
            await CreateSessionAsync(factory, attachedCharacterId, cancellationToken);
        var service = new EventBusStreamService(sessions, registry, clients, new FleetPresenceSourceGuard(), services);

        await service.Attach(
            new SingleMessageReader(new ClientEnvelope { Event = envelope }),
            senderWriter,
            new HeadersOnlyCallContext(new Metadata { { "authorization", $"Bearer {accessToken}" } }));

        Assert.Equal([FleetPermissions.Metrics], policy.CheckedCodes);
        Assert.Equal(permissionAllowed ? 1 : 0, local.Count);
        if (permissionAllowed)
        {
            FleetKillmailShareEvent roundTripped = Assert.Single(local);
            Assert.IsAssignableFrom<IFleetScopedEvent>(roundTripped);
            Assert.Equal(attachedCharacterId, roundTripped.CharacterId);
            Assert.Equal(fleetId, roundTripped.FleetId);
            Assert.Equal(original.Data.UnixMs, roundTripped.Data.UnixMs);
            FleetKillmailReference killmail = Assert.Single(roundTripped.Data.Killmails);
            Assert.Equal(138560925, killmail.KillmailId);
            Assert.Equal("2305db84acc094d6ecfeda59ffcb3b5f70b9eb3f", killmail.Hash);
            Assert.Equal(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), killmail.KillmailTimeUtc);
            Assert.True(killmail.IsLoss);
        }

        Assert.Equal(expectedSenderMessages, CountKillmailShares(senderWriter));
        Assert.Equal(expectedFleetMateMessages, CountKillmailShares(fleetMateWriter));
    }

    private static ServiceProvider BuildServices(
        SqliteServerDbContextFactory factory,
        ConnectedClients clients,
        FleetMetricsPolicy policy)
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
        services.AddSingleton<IAccessPolicy>(policy);
        services.AddSingleton<IPrincipalAccessor>(new FixedPrincipalAccessor());
        return services.BuildServiceProvider();
    }

    private static async Task<(ServerSessionService Sessions, string AccessToken)> CreateSessionAsync(
        SqliteServerDbContextFactory factory,
        int characterId,
        CancellationToken cancellationToken)
    {
        var repository = new ServerAuthRepository(factory);
        var character = await repository.UpsertSyncedAsync(
            characterId,
            $"Character {characterId}",
            new EncryptedToken([1], [2], [3]),
            null,
            cancellationToken);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);
        string accessToken = (await sessions.IssueAsync(character.Id, cancellationToken)).AccessToken;
        return (sessions, accessToken);
    }

    private static int CountKillmailShares(RecordingWriter writer) =>
        writer.Written.Count(message => message.Event.EventType == "fleet.killmail-share");

    private sealed class FleetMetricsPolicy(bool allowed) : IAccessPolicy
    {
        public List<string> CheckedCodes { get; } = [];

        public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default)
        {
            CheckedCodes.Add(code);
            return Task.FromResult(allowed);
        }
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
            if (_wasRead)
            {
                return Task.FromResult(false);
            }

            _wasRead = true;
            return Task.FromResult(true);
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
