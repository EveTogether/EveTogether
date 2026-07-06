using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Pushes a bus keepalive to every attached client on a fixed cadence. A client uses these to tell a
/// live server from one that silently went away: a restart behind cloudflared keeps the client's HTTP/2 stream
/// half-open (the tunnel edge answers transport keepalive, so the dead origin is invisible to the transport),
/// which left the client's read loop wedged in Connected with the server never seeing it again. With the ping in
/// place the client's receive-deadline fires when these stop arriving and it reconnects; a failed push here also
/// evicts a dead (ghost) client from the presence list. The interval is well under the client's deadline so a
/// single dropped ping never trips a reconnect.
/// </summary>
public sealed class EventBusKeepaliveService(ConnectedClients clients, ILogger<EventBusKeepaliveService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await clients.PingAllAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bus keepalive loop stopped unexpectedly.");
        }
    }
}
