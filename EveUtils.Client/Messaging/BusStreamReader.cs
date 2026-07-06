using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Grpc;
using Grpc.Core;

namespace EveUtils.Client.Messaging;

/// <summary>
/// Reads the remote bus response stream with a receive-deadline. gRPC's transport keepalive cannot detect
/// a server that restarted behind a terminating proxy: cloudflared answers the HTTP/2 ping itself, so the
/// half-open stream never faults and a plain <c>await foreach</c> blocks forever — which left the connection
/// wedged in Connected while the server no longer knew about it. The server pushes a periodic
/// <see cref="EveUtils.Shared.Messaging.Wire.BusKeepAlive"/> envelope instead; if neither a keepalive nor a real
/// event arrives within <c>receiveDeadline</c>, the origin is treated as gone and the read throws so
/// the connection loop reconnects.
/// </summary>
public static class BusStreamReader
{
    /// <summary>Advances the reader, bounded by <paramref name="receiveDeadline"/>. Returns the underlying
    /// <see cref="IAsyncStreamReader{T}.MoveNext"/> result (false = the server closed the stream). Throws
    /// <see cref="TimeoutException"/> when nothing arrives in time; a genuine stream fault before the deadline
    /// bubbles unchanged. Cancellation via <paramref name="cancellationToken"/> propagates as cancellation.</summary>
    public static async Task<bool> MoveNextWithDeadlineAsync(
        IAsyncStreamReader<ServerEnvelope> reader, TimeSpan receiveDeadline, CancellationToken cancellationToken)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(receiveDeadline);
        try
        {
            return await reader.MoveNext(deadlineCts.Token);
        }
        catch (Exception) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Our deadline fired (not a real shutdown): MoveNext surfaces this as OperationCanceledException or
            // RpcException(Cancelled) depending on timing — either way it means the server went silent.
            throw new TimeoutException(
                $"No bus traffic (keepalive or event) within {receiveDeadline.TotalSeconds:0}s — server gone?");
        }
    }
}
