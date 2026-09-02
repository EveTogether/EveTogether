using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Manages the remote event-bus connections to the servers a client is coupled to. Multiple characters can
/// be coupled to the same server, and the server tracks presence per character (one stream = one connected
/// character), so this opens <em>one <see cref="ServerConnection"/> per (server, character)</em> — every coupled
/// character shows as connected (B1), not just the most-recent one. Implements both <see cref="IRemoteEventTransport"/>
/// (the bus's outbound seam) and <see cref="IRemoteBusConnector"/> (attach/detach + per-server state). The public
/// surface stays keyed by server address; per-server state is the aggregate of its characters' connections.
/// </summary>
public sealed class RemoteBusConnectionManager(
    GrpcChannelFactory channelFactory,
    IClientSessionStore sessionStore,
    IEventTypeRegistry registry,
    IServiceProvider services) : IRemoteEventTransport, IRemoteBusConnector, ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Server, int Character), ServerConnection> _connections = new();

    public event Action<string, ServerConnectionState> StateChanged = (_, _) => { };
    public event Action<string, int, ServerConnectionState> CharacterStateChanged = (_, _, _) => { };

    public IReadOnlyDictionary<string, ServerConnectionState> States
    {
        get
        {
            lock (_gate)
                return _connections
                    .GroupBy(kv => kv.Key.Server, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => Aggregate(g.Select(kv => kv.Value.State)), StringComparer.OrdinalIgnoreCase);
        }
    }

    public ServerConnectionState StateFor(string serverAddress)
    {
        lock (_gate)
            return Aggregate(_connections
                .Where(kv => string.Equals(kv.Key.Server, serverAddress, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value.State));
    }

    public ServerConnectionState StateFor(string serverAddress, int characterId)
    {
        // Matched the same way the roll-up matches, rather than by dictionary lookup: the key's string comparison is
        // ordinal, and every other lookup here treats two spellings of one host as the same server.
        lock (_gate)
            return _connections
                .Where(kv => kv.Key.Character == characterId
                             && string.Equals(kv.Key.Server, serverAddress, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value.State)
                .DefaultIfEmpty(ServerConnectionState.Disconnected)
                .First();
    }

    /// <summary>
    /// Attaches the remote bus for a server. With <paramref name="preferredCharacterId"/> set (a just-paired char),
    /// starts/restarts only that character's connection and leaves the others live. Without it, reconciles the whole
    /// server: stops every existing connection and opens one per currently-coupled character (startup + after a
    /// decouple, which also clears the decoupled character's now-stale connection).
    /// </summary>
    public async Task AttachAsync(string serverAddress, int? preferredCharacterId = null, CancellationToken cancellationToken = default)
    {
        if (preferredCharacterId is { } only)
        {
            StartConnection(serverAddress, only);
            return;
        }

        var coupled = (await sessionStore.LoadAllAsync(serverAddress, cancellationToken))
            .Select(s => s.CharacterId)
            .ToList();

        lock (_gate)
        {
            foreach (var key in _connections.Keys
                         .Where(k => string.Equals(k.Server, serverAddress, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                if (_connections.Remove(key, out var stale))
                    stale.Stop();
        }

        foreach (var characterId in coupled)
            StartConnection(serverAddress, characterId);

        if (coupled.Count == 0)
            StateChanged(serverAddress, ServerConnectionState.Disconnected); // not paired anymore
    }

    private void StartConnection(string serverAddress, int characterId)
    {
        ServerConnection connection;
        lock (_gate)
        {
            var key = (serverAddress, characterId);
            if (_connections.Remove(key, out var existing))
                existing.Stop();

            connection = new ServerConnection(serverAddress, characterId, channelFactory, sessionStore, registry, services);
            // Both signals, because they answer different questions. The roll-up moves the server-wide indicators;
            // the per-character one is what a link chip follows. Only raising the roll-up is what hid a swept session
            // completely: five healthy characters on the same server roll up to Connected (ET-123).
            connection.StateChanged += state =>
            {
                CharacterStateChanged(serverAddress, characterId, state);
                StateChanged(serverAddress, StateFor(serverAddress));
            };
            _connections[key] = connection;
        }

        connection.Start();
    }

    /// <summary>
    /// Rebuilds every connection this manager holds, for every server it holds one for. Used after the machine wakes
    /// up (ET-121): the sockets died while it slept and the streams only notice when their 45 s receive deadline
    /// lapses — and a connection sitting out a slow retry behind a refused session would wait minutes longer still.
    /// Reconciling per server also picks up the characters, so a pairing repaired while asleep comes back with it.
    /// </summary>
    public async Task ReconnectAllAsync(CancellationToken cancellationToken = default)
    {
        string[] servers;
        lock (_gate)
            servers = _connections.Keys
                .Select(k => k.Server)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var server in servers)
            await AttachAsync(server, cancellationToken: cancellationToken);
    }

    public Task DetachAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        List<int> detached = [];
        lock (_gate)
        {
            foreach (var key in _connections.Keys
                         .Where(k => string.Equals(k.Server, serverAddress, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                if (_connections.Remove(key, out var connection))
                {
                    connection.Stop();
                    detached.Add(key.Character);
                }
        }

        // Say it per character as well: a chip follows its own character now, so without this the links of a
        // decoupled server would keep showing whatever they last were.
        foreach (var characterId in detached)
            CharacterStateChanged(serverAddress, characterId, ServerConnectionState.Disconnected);
        StateChanged(serverAddress, ServerConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        // The server attributes every event to the attached session character and rejects one published over another
        // character's stream (anti-spoof, EventBusStreamService). A character-stamped event (DPS/bounty/location, …)
        // must therefore travel over THAT character's own connection — otherwise multiboxing several characters funnels
        // all their metrics through the first character's stream and the server drops every other character's samples.
        // A character-agnostic event (CharacterId 0) keeps the dedupe: one connection per server is enough, and sending
        // it up every character's stream would make the server reroute the same event once per stream.
        var claimedCharacter = integrationEvent.CharacterId ?? 0;

        ServerConnection[] live;
        lock (_gate)
        {
            var connected = _connections.Values.Where(c => c.State == ServerConnectionState.Connected).ToList();
            var chosen = SelectTargets(connected.Select(c => new ConnectionRef(c.ServerAddress, c.CharacterId)).ToList(),
                claimedCharacter).ToHashSet();
            live = connected.Where(c => chosen.Contains(new ConnectionRef(c.ServerAddress, c.CharacterId))).ToArray();
        }

        if (live.Length == 0)
            return; // no stream for this character/server attached — deliberate no-op

        // Build the wire envelope once, broadcast to every connected server (POC; per-server event
        // scoping is a later seam, still an open point).
        var envelope = new ClientEnvelope { Event = ToEnvelope(integrationEvent) };
        await Task.WhenAll(live.Select(c => c.SendEnvelopeAsync(envelope, cancellationToken)));
    }

    /// <summary>A connected outbound stream's identity (server + the character it is authenticated as).</summary>
    public readonly record struct ConnectionRef(string ServerAddress, int CharacterId);

    /// <summary>
    /// Pure routing rule (anti-spoof, EventBusStreamService): a character-stamped event goes ONLY over that character's
    /// own connection(s) — so multiboxing several characters does not funnel all their metrics through one stream that
    /// the server would reject for every other character. A character-agnostic event (<paramref name="claimedCharacter"/>
    /// 0) is deduped to one connection per server (sending it up every stream would reroute it once per stream).
    /// </summary>
    public static IReadOnlyList<ConnectionRef> SelectTargets(IReadOnlyList<ConnectionRef> connected, int claimedCharacter) =>
        claimedCharacter != 0
            ? connected.Where(c => c.CharacterId == claimedCharacter).ToList()
            : connected
                .GroupBy(c => c.ServerAddress, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

    /// <summary>Per-server state = the best of its characters' connection states (connected if any character is).</summary>
    private static ServerConnectionState Aggregate(IEnumerable<ServerConnectionState> states)
    {
        var list = states.ToList();
        if (list.Count == 0) return ServerConnectionState.Disconnected;
        if (list.Contains(ServerConnectionState.Connected)) return ServerConnectionState.Connected;
        if (list.Contains(ServerConnectionState.Connecting)) return ServerConnectionState.Connecting;
        if (list.Contains(ServerConnectionState.Reconnecting)) return ServerConnectionState.Reconnecting;
        // Ahead of SessionExpired: a refused certificate applies to the whole server rather than to one character's
        // pairing, and re-pairing is pointless until the user has decided the new certificate is the server's.
        if (list.Contains(ServerConnectionState.CertificateRejected)) return ServerConnectionState.CertificateRejected;
        // Ahead of SessionExpired because it is the one that will not clear on its own. Listed at all because a state
        // missing from this ladder falls through to Disconnected — which is how a session the server had dropped
        // reported as a plain amber "disconnected" even when it was the only character on the server (ET-123).
        if (list.Contains(ServerConnectionState.SessionGone)) return ServerConnectionState.SessionGone;
        if (list.Contains(ServerConnectionState.SessionExpired)) return ServerConnectionState.SessionExpired;
        return ServerConnectionState.Disconnected;
    }

    private static EventEnvelope ToEnvelope(IIntegrationEvent integrationEvent)
    {
        var envelope = new EventEnvelope
        {
            EventType = integrationEvent.EventType,
            EventId = integrationEvent.EventId.ToString(),
            CharacterId = integrationEvent.CharacterId ?? 0,
            Timestamp = integrationEvent.Timestamp.ToString("o"),
            PayloadJson = integrationEvent.Data is null
                ? "{}"
                : JsonSerializer.Serialize(integrationEvent.Data, integrationEvent.Data.GetType())
        };

        // Targeted events carry their recipient on the wire so the server reroutes only to that
        // character's connections; untargeted events leave it 0 and broadcast as before.
        if (integrationEvent is ITargetedEvent targeted)
            envelope.TargetCharacterId = targeted.TargetCharacterId;

        // Fleet-scoped events carry their fleet so the server reroutes only to that fleet's active
        // participants. Mutually exclusive with a single-character target in practice.
        if (integrationEvent is IFleetScopedEvent fleetScoped)
            envelope.FleetId = fleetScoped.FleetId;

        return envelope;
    }
}
