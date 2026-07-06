using System.IO;
using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Messaging.Wire;
using Grpc.Core;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The server-side half of the bus keepalive: a live client is pinged so its read-deadline stays fresh,
/// and a client whose stream is dead (the peer vanished behind the tunnel) is evicted from the presence list
/// instead of lingering as a ghost that fails every broadcast.
/// </summary>
public class EventBusKeepalivePingTests
{
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

    private sealed class DeadWriter : IServerStreamWriter<ServerEnvelope>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => throw new IOException("peer gone");
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken) => throw new IOException("peer gone");
    }

    [Fact]
    public async Task PingAll_pushes_a_keepalive_to_a_live_client()
    {
        var clients = new ConnectedClients();
        var writer = new RecordingWriter();
        clients.Add(new ConnectedClient("k1", 100, "Alice", writer));

        await clients.PingAllAsync(TestContext.Current.CancellationToken);

        var envelope = Assert.Single(writer.Written);
        Assert.True(BusKeepAlive.IsKeepAlive(envelope.Event));
        Assert.True(clients.IsConnected(100)); // still present
    }

    [Fact]
    public async Task PingAll_evicts_a_client_whose_stream_is_dead()
    {
        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("dead", 200, "Bob", new DeadWriter()));
        Assert.True(clients.IsConnected(200));

        await clients.PingAllAsync(TestContext.Current.CancellationToken);

        Assert.False(clients.IsConnected(200)); // ghost evicted, broadcasts no longer try to write to it
    }
}
