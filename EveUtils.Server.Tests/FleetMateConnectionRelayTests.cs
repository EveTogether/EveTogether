using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-492: a fleet mate coming online or going away reached nobody — the roster's connection flag was read once and
/// the server said nothing when it changed. The transition is raised on a character's first and last connection only,
/// and goes to the connected co-members of their fleets, never to a stranger or back to themselves.
/// </summary>
public sealed class FleetMateConnectionRelayTests : IDisposable
{
    private const int Jithran = 95200001;
    private const int Raymond = 95200002;
    private const int Stranger = 95200099;

    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly ConnectedClients _clients = new();
    private readonly List<(int CharacterId, bool IsConnected)> _transitions = [];

    public FleetMateConnectionRelayTests() =>
        _clients.CharacterConnectionChanged += (characterId, isConnected) => _transitions.Add((characterId, isConnected));

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void ConnectionChanged_SecondConnectionAndItsDrop_AreNoTransition()
    {
        _clients.Add(new ConnectedClient("j-1", Jithran, "Jithran", new RecordingWriter()));
        _clients.Add(new ConnectedClient("j-2", Jithran, "Jithran", new RecordingWriter()));
        _clients.Remove("j-1");
        _clients.Remove("j-2");
        _clients.Remove("j-2");

        Assert.Equal([(Jithran, true), (Jithran, false)], _transitions);
    }

    [Fact]
    public async Task ConnectionChanged_KeepaliveEvictsTheLastConnection_IsADisconnect()
    {
        _clients.Add(new ConnectedClient("j-1", Jithran, "Jithran", new RecordingWriter { Fails = true }));

        await _clients.PingAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal([(Jithran, true), (Jithran, false)], _transitions);
    }

    [Fact]
    public async Task RelayAsync_ReachesConnectedRosterMatesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _FleetAsync(ct, Jithran, Raymond);
        var jithran = new RecordingWriter();
        var raymond = new RecordingWriter();
        var stranger = new RecordingWriter();
        _clients.Add(new ConnectedClient("jithran", Jithran, "Jithran", jithran));
        _clients.Add(new ConnectedClient("raymond", Raymond, "Raymond", raymond));
        _clients.Add(new ConnectedClient("stranger", Stranger, "Stranger", stranger));
        using var provider = _Provider();
        using var relay = _Relay(provider);

        await relay.RelayAsync(Jithran, isConnected: false, ct);

        Assert.Equal([new FleetMateConnectionPayload(Jithran, false)], raymond.Connections());
        Assert.Empty(jithran.Connections());
        Assert.Empty(stranger.Connections());
    }

    [Fact]
    public async Task Started_AMateConnecting_IsPushedToTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await _FleetAsync(ct, Jithran, Raymond);
        var raymond = new RecordingWriter();
        _clients.Add(new ConnectedClient("raymond", Raymond, "Raymond", raymond));
        using var provider = _Provider();
        using var relay = _Relay(provider);
        await relay.StartAsync(ct);

        _clients.Add(new ConnectedClient("jithran", Jithran, "Jithran", new RecordingWriter()));
        await raymond.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal([new FleetMateConnectionPayload(Jithran, true)], raymond.Connections());
    }

    private async Task _FleetAsync(CancellationToken cancellationToken, params int[] members)
    {
        var repository = new FleetRepository(_factory);
        var now = DateTimeOffset.UtcNow;
        var fleetId = await repository.AddAsync(new FleetEntity
        {
            Name = "Sikrah misc",
            CreatorCharacterId = members[0],
            State = FleetState.Active,
            Activation = FleetActivation.Forming,
            CreatedAt = now,
            LastActivityAt = now
        }, cancellationToken);
        foreach (var characterId in members)
            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId,
                CharacterId = characterId,
                Role = characterId == members[0] ? FleetRole.FleetCommander : FleetRole.SquadMember,
                JoinTime = now
            }, cancellationToken);
    }

    private ServiceProvider _Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_clients);
        services.AddSingleton<IFleetReader>(new FleetRepository(_factory));
        services.AddScoped<FleetBroadcastResolver>();
        return services.BuildServiceProvider();
    }

    private FleetMateConnectionRelay _Relay(ServiceProvider provider) =>
        new(_clients, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FleetMateConnectionRelay>.Instance);

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        private readonly List<ServerEnvelope> _written = [];

        public bool Fails { get; init; }
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ServerEnvelope message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            if (Fails)
                throw new IOException("peer gone");
            lock (_written)
                _written.Add(message);
            FirstWrite.TrySetResult();
            return Task.CompletedTask;
        }

        public List<FleetMateConnectionPayload> Connections()
        {
            lock (_written)
                return [.. _written
                    .Where(w => w.Event.EventType == "fleet.mate-connection")
                    .Select(w => JsonSerializer.Deserialize<FleetMateConnectionPayload>(w.Event.PayloadJson)
                                 ?? throw new InvalidOperationException("empty payload"))];
        }
    }
}
