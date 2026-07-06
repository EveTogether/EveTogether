using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// Fans the realtime stream out to the connected <c>/ws</c> widgets. It is a read-only OUTPUT — it never measures
/// anything itself. Own-character combat metrics are polled from the existing query bridge on a 1 Hz timer (the
/// always-on baseline, fleet or not); fleet metric/roster changes are forwarded from the in-process
/// <see cref="IEventBus"/> the fleet windows already publish on. Each client gets a <c>snapshot</c> on connect, then
/// the live <c>{ type, data, ts }</c> stream. Tied to the host lifecycle: started after the Kestrel host starts and
/// stopped before it stops. (No <c>fit.changed</c> event exists yet, so that stream is intentionally absent.)
/// </summary>
public sealed class LocalApiBroadcaster
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _rootServices;
    private readonly LocalApiQueries _queries;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, WebSocket> _clients = [];
    private readonly object _clientsGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _subscriptions = [];
    private Task? _metricsLoop;
    private IHubContext<FleetHub>? _hub;
    private int _signalRCount;

    public LocalApiBroadcaster(IServiceProvider rootServices, ILogger logger)
    {
        _rootServices = rootServices;
        _queries = new LocalApiQueries(rootServices);
        _logger = logger;
    }

    /// <summary>Wires the SignalR hub once the host is built, so live events also reach <c>/hub/fleet</c> clients.</summary>
    public void AttachHub(IHubContext<FleetHub> hub) => _hub = hub;

    /// <summary>The SignalR hub reports its connect/disconnect so the 1 Hz tick runs while any client — WS or hub — listens.</summary>
    public void SignalRConnected() => Interlocked.Increment(ref _signalRCount);
    public void SignalRDisconnected() => Interlocked.Decrement(ref _signalRCount);

    /// <summary>Subscribes to the fleet event stream and starts the own-metrics tick. Idempotent per instance.</summary>
    public void Start()
    {
        if (_rootServices.GetService<IEventBus>() is { } bus)
        {
            _subscriptions.Add(bus.Subscribe<FleetMetricEvent>((e, _) =>
                _BroadcastAsync("fleet.metrics", FleetMetricSampleDto.FromSample(e.Data), e.Data.UnixMs)));
            _subscriptions.Add(bus.Subscribe<FleetChangedEvent>((e, _) =>
                _BroadcastAsync("fleet.changed", FleetChangedDto.FromEvent(e), _NowMs())));
        }
        _metricsLoop = Task.Run(() => _MetricsLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();

        if (_metricsLoop is not null)
        {
            try { await _metricsLoop; } catch { /* loop cancelled */ }
        }

        foreach (var socket in _Snapshot())
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None); }
            catch { /* already gone */ }
        }
        lock (_clientsGate) _clients.Clear();
        _cts.Dispose();
        _sendGate.Dispose();
    }

    /// <summary>Serves one accepted WebSocket: sends a snapshot, registers it for broadcasts, then parks on a receive
    /// loop until the client disconnects so we notice the close and stop writing to a dead socket.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        try
        {
            await _SendSnapshotAsync(socket, cancellationToken);
            lock (_clientsGate) _clients[id] = socket;
            await _ReceiveUntilCloseAsync(socket, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client disconnected mid-stream — expected, not an error.
        }
        finally
        {
            lock (_clientsGate) _clients.Remove(id);
        }
    }

    private async Task _SendSnapshotAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var snapshot = new WsSnapshotDto(
            await _queries.GetMetricsAsync(cancellationToken),
            await _queries.GetActiveFleetAsync(cancellationToken));
        var json = _Envelope("snapshot", snapshot, _NowMs());

        await _sendGate.WaitAsync(cancellationToken);
        try { await _SendRawAsync(socket, json, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private async Task _MetricsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MetricsInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_HasNoListeners()) continue; // no listeners → don't poll the gamelog for no one
            try
            {
                var metrics = await _queries.GetMetricsAsync(cancellationToken);
                await _BroadcastAsync("metrics", metrics, _NowMs());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Local API metrics tick failed.");
            }
        }
    }

    private async Task _BroadcastAsync(string type, object data, long ts)
    {
        if (_cts.IsCancellationRequested) return;

        // SignalR path: independent of the raw-WS send gate; idiomatic named-event dispatch ("metrics" etc.).
        if (_hub is { } hub && Volatile.Read(ref _signalRCount) > 0)
        {
            try { await hub.Clients.All.SendAsync(type, data, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Local API hub broadcast failed."); }
        }

        // Raw WebSocket path: one { type, data, ts } envelope to every open socket.
        if (_IsWsEmpty()) return;
        var json = _Envelope(type, data, ts);
        try { await _sendGate.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            foreach (var (id, socket) in _SnapshotWithIds())
            {
                if (socket.State != WebSocketState.Open) { _Remove(id); continue; }
                try { await _SendRawAsync(socket, json, _cts.Token); }
                catch { _Remove(id); }
            }
        }
        finally { _sendGate.Release(); }
    }

    private static async Task _SendRawAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task _ReceiveUntilCloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                return;
            }
            // Read-only API: anything the client sends is ignored (the receive loop only exists to detect the close).
        }
    }

    private static string _Envelope(string type, object data, long ts)
    {
        // Serialize the payload by its runtime type first so an `object`-typed Data property still serializes fully.
        var element = JsonSerializer.SerializeToElement(data, data.GetType(), JsonOptions);
        return JsonSerializer.Serialize(new { type, data = element, ts }, JsonOptions);
    }

    private static long _NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private bool _IsWsEmpty() { lock (_clientsGate) return _clients.Count == 0; }
    private bool _HasNoListeners() => _IsWsEmpty() && Volatile.Read(ref _signalRCount) <= 0;
    private void _Remove(Guid id) { lock (_clientsGate) _clients.Remove(id); }
    private List<WebSocket> _Snapshot() { lock (_clientsGate) return [.. _clients.Values]; }
    private List<(Guid Id, WebSocket Socket)> _SnapshotWithIds()
    {
        lock (_clientsGate)
        {
            var list = new List<(Guid, WebSocket)>(_clients.Count);
            foreach (var (id, socket) in _clients) list.Add((id, socket));
            return list;
        }
    }
}
