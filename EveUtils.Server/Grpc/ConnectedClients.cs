using System.Collections.Concurrent;
using EveUtils.Grpc;
using EveUtils.Shared.Messaging.Wire;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Tracks attached clients (presence source for the admin panel) and reroutes events. The default
/// reroute broadcasts to every connected client except the originator; a secondary index on character id
/// lets the server deliver a single event only to a given character's connections (targeted/fleet-scoped
/// routing). A character may hold several connections at once (multiple devices / a reconnect race),
/// so a targeted send fans out to all of them.
/// </summary>
public sealed class ConnectedClients
{
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();

    // Secondary index: character id → its live connections (keyed by the same connection key). Empty
    // buckets are left in place deliberately — removing them races a concurrent Add for the same
    // character, and the bucket count is bounded by the (small) number of distinct characters.
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, ConnectedClient>> _byCharacter = new();

    public void Add(ConnectedClient client)
    {
        _clients[client.Key] = client;
        if (client.CharacterId != 0)
            _byCharacter.GetOrAdd(client.CharacterId, _ => new()).AddOrUpdate(client.Key, client, (_, _) => client);
    }

    public void Remove(string key)
    {
        if (_clients.TryRemove(key, out var client)
            && client.CharacterId != 0
            && _byCharacter.TryGetValue(client.CharacterId, out var connections))
        {
            connections.TryRemove(key, out _);
        }
    }

    public IReadOnlyList<ConnectedClientInfo> Snapshot() =>
        _clients.Values.Select(c => new ConnectedClientInfo(c.CharacterName, c.ConnectedAt)).ToList();

    /// <summary>The distinct characters currently connected (id + name), for invite discovery.
    /// Skips empty index buckets left behind by a disconnect.</summary>
    public IReadOnlyList<ConnectedCharacterInfo> ConnectedCharacters() =>
        _byCharacter
            .Where(bucket => !bucket.Value.IsEmpty)
            .Select(bucket => new ConnectedCharacterInfo(bucket.Key, bucket.Value.Values.First().CharacterName))
            .ToList();

    /// <summary>True if the character currently holds at least one live connection.</summary>
    public bool IsConnected(int characterId) =>
        characterId != 0 && _byCharacter.TryGetValue(characterId, out var connections) && !connections.IsEmpty;

    /// <summary>Pushes a bus keepalive to every connected client. A client whose write fails is evicted:
    /// its stream is dead (the peer vanished — typically behind the tunnel), so it would otherwise linger in the
    /// presence list and fail every subsequent broadcast. Only a live origin can emit these, so on the client side
    /// their absence within the receive-deadline is what reveals a server that silently went away.</summary>
    public async Task PingAllAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _clients.Values)
            if (!await TryWriteAsync(client, new EventEnvelope { EventType = BusKeepAlive.EventType }, cancellationToken))
                Remove(client.Key);
    }

    /// <summary>Reroutes an event to every connected client except the originator (no echo back).</summary>
    public async Task BroadcastExceptAsync(string exceptKey, EventEnvelope eventEnvelope, CancellationToken cancellationToken)
    {
        foreach (var client in _clients.Values)
        {
            if (client.Key == exceptKey)
                continue;

            await TryWriteAsync(client, eventEnvelope, cancellationToken);
        }
    }

    /// <summary>Delivers an event only to the given character's connections. No-op if the
    /// character is not connected.</summary>
    public Task SendToCharacterAsync(int characterId, EventEnvelope eventEnvelope, CancellationToken cancellationToken) =>
        SendToCharactersAsync([characterId], eventEnvelope, cancellationToken);

    /// <summary>Delivers an event to the connections of each given character (deduplicated). Used for
    /// fleet-scoped delivery to a fleet's active participants. <paramref name="exceptKey"/> skips the
    /// originating connection so a publisher does not get its own event echoed back.</summary>
    public async Task SendToCharactersAsync(
        IEnumerable<int> characterIds, EventEnvelope eventEnvelope, CancellationToken cancellationToken, string? exceptKey = null)
    {
        foreach (var characterId in characterIds.Distinct())
        {
            if (characterId == 0 || !_byCharacter.TryGetValue(characterId, out var connections))
                continue;

            foreach (var client in connections.Values)
            {
                if (client.Key == exceptKey)
                    continue;

                await TryWriteAsync(client, eventEnvelope, cancellationToken);
            }
        }
    }

    /// <summary>Writes one envelope to a client's stream, serialised by its <see cref="ConnectedClient.WriteGate"/>
    /// (gRPC allows a single concurrent writer per stream). Returns <c>false</c> if the write failed — a broken
    /// peer. Broadcasts ignore the result (cleanup follows when the Attach loop ends); the keepalive ping uses it
    /// to evict a dead client.</summary>
    private static async Task<bool> TryWriteAsync(ConnectedClient client, EventEnvelope eventEnvelope, CancellationToken cancellationToken)
    {
        await client.WriteGate.WaitAsync(cancellationToken);
        try
        {
            await client.Writer.WriteAsync(new ServerEnvelope { Event = eventEnvelope }, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            client.WriteGate.Release();
        }
    }
}
