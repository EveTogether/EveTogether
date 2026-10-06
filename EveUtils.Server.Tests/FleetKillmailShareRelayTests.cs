using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Grpc.Core;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

public sealed class FleetKillmailShareRelayTests
{
    private const int Sender = 95110001;
    private const int FleetMate = 95110002;
    private const int Stranger = 95110099;

    [Theory]
    [InlineData(true, true, 1, 1)]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, true, 0, 0)]
    public async Task WireAndRelay_MembershipAndActivationScenario_RoundTripsAndReachesExpectedConnections(
        bool senderIsMember,
        bool active,
        int expectedSenderMessages,
        int expectedFleetMateMessages)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var original = new FleetKillmailShareEvent(new FleetKillmailShare
        {
            FleetId = 42,
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
        var registry = new EventTypeRegistry();
        new FleetWireEvents().RegisterInto(registry);

        IIntegrationEvent deserialized = registry.Deserialize(envelope.EventType, envelope.PayloadJson, envelope.CharacterId)
                                         ?? throw new InvalidOperationException("The fleet killmail share was not registered.");
        var roundTripped = Assert.IsType<FleetKillmailShareEvent>(deserialized);
        Assert.IsAssignableFrom<IFleetScopedEvent>(roundTripped);
        Assert.Equal(Sender, roundTripped.CharacterId);
        Assert.Equal(original.FleetId, roundTripped.FleetId);
        Assert.Equal(original.Data.UnixMs, roundTripped.Data.UnixMs);
        FleetKillmailReference killmail = Assert.Single(roundTripped.Data.Killmails);
        Assert.Equal(138560925, killmail.KillmailId);
        Assert.Equal("2305db84acc094d6ecfeda59ffcb3b5f70b9eb3f", killmail.Hash);
        Assert.Equal(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), killmail.KillmailTimeUtc);
        Assert.True(killmail.IsLoss);

        using var factory = new SqliteServerDbContextFactory();
        IFleetRepository repository = new FleetRepository(factory);
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

        var senderWriter = new RecordingWriter();
        var fleetMateWriter = new RecordingWriter();
        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("sender", attachedCharacterId, "Sender", senderWriter));
        clients.Add(new ConnectedClient("fleet-mate", FleetMate, "Fleet mate", fleetMateWriter));
        var resolver = new FleetBroadcastResolver(repository, clients);
        IReadOnlyList<int> recipients = await resolver.IsMemberAsync(fleetId, attachedCharacterId, cancellationToken)
            ? await resolver.ActiveBroadcastMembersAsync(fleetId, cancellationToken)
            : [];
        envelope.FleetId = fleetId;

        await clients.SendToCharactersAsync(
            recipients,
            envelope,
            cancellationToken,
            EventBusStreamService.SenderExclusionKey(roundTripped, "sender"));

        Assert.Equal(expectedSenderMessages, senderWriter.Written.Count);
        Assert.Equal(expectedFleetMateMessages, fleetMateWriter.Written.Count);
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
}
