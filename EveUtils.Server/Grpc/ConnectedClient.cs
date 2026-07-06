using EveUtils.Grpc;
using Grpc.Core;

namespace EveUtils.Server.Grpc;

/// <summary>One attached client's outbound stream. Its <see cref="WriteGate"/> serialises writes
/// (gRPC requires a single concurrent writer per stream).</summary>
public sealed class ConnectedClient(string key, int characterId, string characterName, IServerStreamWriter<ServerEnvelope> writer)
{
    public string Key { get; } = key;

    /// <summary>The attached character's ESI id (0 = unknown). Used to reroute targeted/fleet-scoped
    /// events to a specific character's connections rather than broadcasting.</summary>
    public int CharacterId { get; } = characterId;

    public string CharacterName { get; } = characterName;
    public IServerStreamWriter<ServerEnvelope> Writer { get; } = writer;
    public SemaphoreSlim WriteGate { get; } = new(1, 1);
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
}
