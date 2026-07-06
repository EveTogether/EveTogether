using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Grpc;
using Grpc.Core;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The client-side half of the bus keepalive: the read-deadline that turns a silently half-open stream
/// (server restarted behind the tunnel, so transport keepalive can't see the dead origin) into a reconnect
/// instead of a wedge in Connected. Before this, the read blocked forever on a stream that never faulted.
/// </summary>
public class BusStreamReaderTests
{
    // MoveNext blocks until its token cancels — models the silently half-open stream: no data, no fault.
    private sealed class SilentReader : IAsyncStreamReader<ServerEnvelope>
    {
        public ServerEnvelope Current => throw new InvalidOperationException();
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var blocked = new TaskCompletionSource();
            using (cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken)))
                await blocked.Task;
            return true; // unreachable: completes only via cancellation
        }
    }

    private sealed class QueuedReader(Queue<ServerEnvelope> items) : IAsyncStreamReader<ServerEnvelope>
    {
        public ServerEnvelope Current { get; private set; } = new();
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (items.Count == 0) return Task.FromResult(false);
            Current = items.Dequeue();
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_silent_stream_times_out_within_the_deadline_instead_of_wedging()
    {
        var token = TestContext.Current.CancellationToken;

        var read = BusStreamReader.MoveNextWithDeadlineAsync(new SilentReader(), TimeSpan.FromMilliseconds(200), token);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), token));

        Assert.Same(read, finished); // the deadline fired well within 5s (without it this read would never return)
        await Assert.ThrowsAsync<TimeoutException>(() => read);
    }

    [Fact]
    public async Task A_delivered_envelope_is_returned_before_the_deadline()
    {
        var reader = new QueuedReader(new Queue<ServerEnvelope>(
            [new ServerEnvelope { Event = new EventEnvelope { EventType = "fleet.changed" } }]));

        Assert.True(await BusStreamReader.MoveNextWithDeadlineAsync(
            reader, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("fleet.changed", reader.Current.Event.EventType);
    }

    [Fact]
    public async Task A_server_closed_stream_returns_false()
    {
        var reader = new QueuedReader(new Queue<ServerEnvelope>());

        Assert.False(await BusStreamReader.MoveNextWithDeadlineAsync(
            reader, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }
}
