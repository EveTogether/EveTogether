using System;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Transport;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GrpcEventBus = EveUtils.Grpc.EventBusStream;
using GrpcSession = EveUtils.Grpc.Session;

namespace EveUtils.Client.Messaging;

/// <summary>
/// One live connection to a single server's remote event bus for a single coupled character. Owns its
/// own gRPC bidi stream, connect/auto-reconnect loop and silent session-refresh. Several characters
/// can be coupled to the same server, so there is one <see cref="ServerConnection"/> per (server, character), all
/// managed by <see cref="RemoteBusConnectionManager"/> — each authenticates as its own character so the server tracks
/// every coupled character as connected (B1). Inbound server events are re-published on the shared LOCAL bus, so
/// subscribers can't tell which server (or local) an event came from.
/// </summary>
public sealed class ServerConnection
{
    // Reconnect backoff: immediate, then growing, capped at 30 s. The cap is not free to raise. One reconnect cycle
    // costs ReceiveDeadline + backoff + ConnectTimeout, and for as long as it runs this character publishes nothing,
    // so every other client reads them as silent. FleetMemberPresence.SilentAfter (90 s, ET-70) is the budget that
    // has to hold: 45 + 30 + 5 = 80 s does, 45 + 60 + 5 = 110 s would have dropped a pilot who is flying perfectly
    // well off every other client's fleet screen. The 60 s step this table used to end on was never reached because
    // the backoff could not grow past 1 s (ET-95) — fixing that is what made the cap matter.
    internal static readonly int[] BackoffSeconds = [0, 1, 3, 7, 15, 30];

    // After this many consecutive failed reconnects, drop the cached channel and rebuild it: a long-lived channel can
    // wedge on a dead connection after a server restart (esp. through the Cloudflare tunnel) and never recover on its
    // own — reusing it retries the dead connection forever, which previously only a client restart cleared.
    private const int ReconnectAttemptsBeforeChannelReset = 2;

    private readonly string _serverAddress;
    private readonly int _characterId;
    private readonly GrpcChannelFactory _channelFactory;
    private readonly IClientSessionStore _sessionStore;
    private readonly IEventTypeRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly IServerTrustStore _trustStore;
    private readonly ILogger<ServerConnection> _logger;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ServerSessionRefresher _refresher;
    private AsyncDuplexStreamingCall<ClientEnvelope, ServerEnvelope>? _call;
    private CancellationTokenSource? _connectionCts;

    public ServerConnection(
        string serverAddress,
        int characterId,
        GrpcChannelFactory channelFactory,
        IClientSessionStore sessionStore,
        IEventTypeRegistry registry,
        IServiceProvider services)
    {
        _serverAddress = serverAddress;
        _characterId = characterId;
        _channelFactory = channelFactory;
        _sessionStore = sessionStore;
        _registry = registry;
        _services = services;
        _trustStore = services.GetRequiredService<IServerTrustStore>();
        _refresher = services.GetRequiredService<ServerSessionRefresher>();
        _logger = services.GetRequiredService<ILogger<ServerConnection>>();
    }

    public string ServerAddress => _serverAddress;

    /// <summary>The coupled character this stream is authenticated as — the server attributes every event published
    /// over it to this character, so a character-stamped event must travel over its own character's connection.</summary>
    public int CharacterId => _characterId;

    public ServerConnectionState State { get; private set; } = ServerConnectionState.Disconnected;
    public event Action<ServerConnectionState> StateChanged = _ => { };

    private void SetState(ServerConnectionState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged(state); } catch { /* subscribers must not break the loop */ }
    }

    /// <summary>Starts the managed connect/auto-reconnect loop. Cancels any previous loop first.</summary>
    public void Start()
    {
        _connectionCts?.Cancel();
        _connectionCts = new CancellationTokenSource();
        var token = _connectionCts.Token;
        _ = Task.Run(() => ConnectLoopAsync(token));
        _ = Task.Run(() => HeartbeatLoopAsync(token)); // independent backup liveness + admin-panel last-seen
    }

    /// <summary>Stops the connection loop and closes the stream (used by decouple).</summary>
    public void Stop()
    {
        _connectionCts?.Cancel();
        _call = null;
        SetState(ServerConnectionState.Disconnected);
    }

    // A coupled-but-unreachable server connects lazily: its very first stream write blocks on the TCP connect
    // (SocketException 10060, ~21s) while the connection still reports Connected. Because the bus fans a publish
    // out to every server with one Task.WhenAll, that single dead server would stall the shared outbound bus —
    // starving the live servers (e.g. the local DPS stream dries up the moment a second, unreachable server is
    // coupled). Bound each write so a dead/slow server fails fast (and drops to reconnect) instead.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    // Max time to confirm a channel is reachable before reporting Connected (gates out unreachable couplings).
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Writes an envelope up this server's stream. No-op until attached (not yet connected). A write that
    /// exceeds <see cref="WriteTimeout"/> (an unreachable server) fails fast as a <see cref="TimeoutException"/> so
    /// it can't stall publishes to the other coupled servers.</summary>
    public async Task SendEnvelopeAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var call = _call;
        if (call is null)
            return; // not attached yet — deliberate no-op

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCts.CancelAfter(WriteTimeout);
            try
            {
                await call.RequestStream.WriteAsync(envelope, writeCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own write deadline fired (not a real shutdown). Drop this connection out of the live set so
                // the next publish skips it entirely — otherwise every publish would keep paying the deadline while
                // the dead server lingers in Connected. The read loop's own failure drives the real reconnect.
                SetState(ServerConnectionState.Reconnecting);
                // Surface as a non-cancellation failure so the publisher logs + continues rather than treating it as "stop".
                throw new TimeoutException($"Write to {_serverAddress} exceeded {WriteTimeout.TotalSeconds:0}s — server unreachable?");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Managed connect + auto-reconnect. Attaches the bidi stream; when it drops, reconnects with
    /// an increasing backoff up to 30 s, until cancelled. Attaches with the preferred character's session
    /// when given (e.g. the just-paired char), else the most recent.
    /// <para>One failure ends the loop instead of retrying, because no further attempt could succeed and the next one
    /// would be refused identically: a certificate the pin refuses (<see cref="ServerConnectionState.CertificateRejected"/>).
    /// A rejected refresh (<see cref="ServerConnectionState.SessionExpired"/>) does NOT end it — it is shown and then
    /// retried on a slow cadence, with the stored pairing left alone (ET-121).</para>
    /// </summary>
    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? attachedAccessToken = null;
            var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            if (delay > 0)
            {
                SetState(ServerConnectionState.Reconnecting);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                var session = await _sessionStore.LoadForCharacterAsync(_serverAddress, _characterId, cancellationToken);
                if (session is null) { SetState(ServerConnectionState.Disconnected); return; } // not paired
                attachedAccessToken = session.AccessToken;

                SetState(ServerConnectionState.Connecting);
                var channel = _channelFactory.CreatePinned(_serverAddress);

                // Actually establish the connection before reporting Connected. gRPC channels connect lazily, so
                // without this a coupled-but-unreachable server would sit in Connected and stall the shared
                // outbound bus on every write (the bus fans each publish to all servers at once). If it can't
                // connect within the window, this throws → reconnect with backoff, never marked Connected/live.
                // It proves reachability and nothing more: it reports Ready as soon as the socket is up, with the
                // TLS handshake still ahead of it — which is why the backoff reset below cannot live here (ET-95).
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectCts.CancelAfter(ConnectTimeout);
                    await channel.ConnectAsync(connectCts.Token);
                }

                var client = new GrpcEventBus.EventBusStreamClient(channel);
                var headers = new Metadata { { "authorization", $"Bearer {session.AccessToken}" } };

                using var call = client.Attach(headers, cancellationToken: cancellationToken);
                _call = call;
                SetState(ServerConnectionState.Connected);

                // The backoff resets on the first message off the wire, not here: attaching only means the socket
                // and the stream object exist. A server presenting a certificate the pin refuses got this far every
                // round, so resetting here pinned the backoff at 1 s, kept every failure at Warning and left the
                // channel rebuild (which tests attempt) permanently unreachable (ET-95).
                await ReadLoopAsync(call, () => attempt = 0, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException rpc) when (rpc.StatusCode == StatusCode.Unauthenticated)
            {
                // Session access token expired/revoked. Refresh through the shared refresher, which serialises this
                // against the heartbeat below and against every unary client's own refresh-on-401.
                switch ((await _refresher.TryRefreshAsync(_serverAddress, _characterId, attachedAccessToken, cancellationToken)).Outcome)
                {
                    case ServerSessionRefreshOutcome.Refreshed:
                        attempt = 0; // refreshed → retry attach immediately
                        continue;
                    case ServerSessionRefreshOutcome.Unavailable:
                        // The server was unreachable while refreshing — keep the session and retry with
                        // backoff. Reopening the app after days must not log the user out just because the
                        // first refresh round-trip hiccuped.
                        SetState(ServerConnectionState.Reconnecting);
                        attempt++;
                        continue;
                    case ServerSessionRefreshOutcome.SessionGone:
                        // The server does not merely refuse the token — it says the session itself is not there any
                        // more. Retrying cannot bring it back, so this ends the loop the way a refused certificate
                        // does, and the chip asks the user to couple again instead of claiming to be busy. The
                        // stored pairing still stays: coupling again overwrites it, and deleting it here would put
                        // back exactly the silent unpairing ET-121 removed.
                        _logger.LogWarning(
                            "Server {Server} no longer has the session for character {Character} — it was cleaned up, revoked or has lapsed. "
                            + "Automatic reconnecting stopped; couple this character to the server again.",
                            _serverAddress, _characterId);
                        SetState(ServerConnectionState.SessionGone);
                        return;
                    default: // Rejected: the server does not recognise the stored refresh token.
                        // The stored pairing STAYS. This branch used to call RemoveAsync, which turned a single
                        // refused round-trip into a pairing the user had to rebuild by hand — the next pass found no
                        // session, reported "not paired" and ended the loop for good (ET-121). A rejection is a state
                        // to show, not a verdict: the server refuses any refresh token missing from its table, and one
                        // goes missing whenever a rotation this client made never reached disk. Keep the credentials,
                        // say so on the chip, and keep trying on a slow cadence so a server that comes back with the
                        // session — or a user who re-pairs — recovers without anyone restarting the app.
                        SetState(ServerConnectionState.SessionExpired);
                        try { await Task.Delay(SessionExpiredRetryInterval, cancellationToken); }
                        catch (OperationCanceledException) { return; }
                        continue;
                }
            }
            catch (Exception ex) when (IsCertificateRejected(ex))
            {
                // The certificate the server presents no longer matches the fingerprint pinned at pairing. Unlike
                // everything in the transient branch below, the next attempt is refused in exactly the same way, so
                // this stops instead of retrying — and says so, with the fingerprint the server offers now. A silent
                // stop would be no improvement on the invisible 1 Hz loop it replaces: this is the one value the
                // user has to check against the server itself before trusting it again.
                _logger.LogError(ex,
                    "Bus connection to {Server} (character {Character}) stopped: the server's TLS certificate does not "
                    + "match the pinned one. Pinned {Pinned}, now presented {Presented}. Re-pair only once you have "
                    + "confirmed the new fingerprint with the server itself.",
                    _serverAddress, _characterId,
                    _trustStore.GetFingerprint(_serverAddress) ?? "(none)",
                    _channelFactory.PresentedFingerprint(_serverAddress) ?? "(unknown)");
                SetState(ServerConnectionState.CertificateRejected);
                return;
            }
            catch (Exception ex)
            {
                // Transient (network/server down/dropped stream) → reconnect with backoff. Log the first drop (we were
                // Connected, attempt 0) at Warning so it's visible in the log window; stay at Debug on the following
                // retries so a longer outage doesn't flood it. This loop used to swallow every failure silently.
                if (attempt == 0)
                    _logger.LogWarning(ex, "Bus connection to {Server} (character {Character}) dropped; reconnecting.", _serverAddress, _characterId);
                else
                    _logger.LogDebug(ex, "Bus reconnect to {Server} attempt {Attempt} failed.", _serverAddress, attempt + 1);
            }
            finally
            {
                _call = null;
            }

            if (cancellationToken.IsCancellationRequested) return;
            SetState(ServerConnectionState.Reconnecting);
            attempt++;

            // Once reconnects keep failing, the cached channel is likely wedged on a dead connection that won't recover
            // on its own — drop it so the next attempt builds a fresh one (what a client restart did, now automatic).
            if (attempt % ReconnectAttemptsBeforeChannelReset == 0)
            {
                // Damped like the failure above, and for the same reason: the counter used to be stuck at 1, so this
                // rebuild never happened at all (ET-95). Now that it does, it repeats for as long as the server stays
                // down — say it once per outage at Warning, then keep it at Debug rather than filling the log window.
                if (attempt == ReconnectAttemptsBeforeChannelReset)
                    _logger.LogWarning("Bus connection to {Server} still down after {Attempt} attempts; rebuilding the channel.", _serverAddress, attempt);
                else
                    _logger.LogDebug("Bus connection to {Server} still down after {Attempt} attempts; rebuilding the channel again.", _serverAddress, attempt);
                _channelFactory.Invalidate(_serverAddress);
            }
        }
    }

    /// <summary>
    /// Whether a failed attach was refused because the server's certificate did not match the pin. Classified on the
    /// exception chain rather than the status code, because gRPC reports a refused handshake as
    /// <see cref="StatusCode.Internal"/> — the same status it gives anything it cannot place, so the code cannot tell
    /// the two apart. The <see cref="AuthenticationException"/> the TLS stack threw is the reliable marker, and on an
    /// <see cref="RpcException"/> it hangs off <c>Status.DebugException</c>, not <c>InnerException</c>.
    /// <para>Deliberately narrow: <c>PermissionDenied</c> and <c>Unimplemented</c> read as permanent but also come
    /// from a proxy that is briefly serving something else, and a wrong verdict here costs the user a connection that
    /// stays down until they act.</para>
    /// </summary>
    internal static bool IsCertificateRejected(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
                return true;
            if (current is RpcException rpc && rpc.Status.DebugException is { } debug && debug != rpc
                && IsCertificateRejected(debug))
                return true;
        }
        return false;
    }

    // How long to wait before re-trying a refresh the server rejected. Long, because the usual cause of a rejection
    // is a stored token the server has already rotated away, which no amount of retrying repairs — but not infinite,
    // because the pairing is deliberately kept now, and something that is kept has to have a way back. At this
    // cadence a permanently stale session costs twelve refused round-trips an hour and stays visible on the chip.
    private static readonly TimeSpan SessionExpiredRetryInterval = TimeSpan.FromMinutes(5);

    // Independent backup liveness: a periodic unary Session.Heartbeat on its own call, separate from
    // the bus stream. It keeps the server's LastHeartbeat fresh (admin-panel "last seen") and gives a coarse
    // reachability signal even if the stream path ever has a gap. A transport failure is only logged at Debug (an
    // outage must not flood the log window) — the bus read-deadline owns reconnects.
    //
    // It is also the only thing that notices the 1-hour access token expiring while the bus stream stays healthy
    // (ET-77). The stream is authenticated ONCE, at attach, so it keeps delivering fleet/fit events for as long as it
    // lives — but every unary RPC re-validates the token on each call, and the server answers an expired one with a
    // normal reply carrying "Not authenticated — pair with the server first." rather than a gRPC Unauthenticated
    // status. So the refresh-on-401 in FleetClient/ServerFitShareClient never fires, the reads that fail do so
    // silently (mapped to an empty list), and the first thing that TELLS the user is a save. This loop already asks
    // the server "is this token still good?" every 30s; acting on the answer is what keeps the token alive.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var session = await _sessionStore.LoadForCharacterAsync(_serverAddress, _characterId, cancellationToken);
                    if (session is null)
                        continue; // not paired (yet) — nothing to keep alive

                    var channel = _channelFactory.CreatePinned(_serverAddress);
                    var client = new GrpcSession.SessionClient(channel);
                    var reply = await client.HeartbeatAsync(
                        new HeartbeatRequest { SessionToken = session.AccessToken }, cancellationToken: cancellationToken);
                    if (reply.Ok)
                    {
                        // Catch the session's own id in passing. A client paired before the server handed it out has
                        // nothing to name its session with, and until it does, a refusal cannot be told apart from a
                        // stale copy — this closes that gap within one tick instead of at the next rotation, which
                        // for a session that is about to be refused may never come.
                        if (reply.SessionId > 0 && reply.SessionId != session.ServerSessionId)
                            await _sessionStore.SetServerSessionIdAsync(
                                _serverAddress, _characterId, reply.SessionId, cancellationToken);
                        continue;
                    }

                    // The server reached us and does not accept this access token any more.
                    switch ((await _refresher.TryRefreshAsync(_serverAddress, _characterId, session.AccessToken, cancellationToken)).Outcome)
                    {
                        case ServerSessionRefreshOutcome.Refreshed:
                            _logger.LogInformation(
                                "Server session for {Server} (character {Character}) was refreshed after its access token expired.",
                                _serverAddress, _characterId);
                            break;
                        case ServerSessionRefreshOutcome.Rejected:
                            // The server does not recognise the stored refresh token. Say so NOW, on the character's
                            // server chip, instead of letting the user discover it on a failed save — and say it at
                            // Warning, because until ET-121 nothing in the log marked the moment a pairing went bad.
                            // The stored session stays put; the connect loop retries it slowly.
                            if (State != ServerConnectionState.SessionExpired)
                                _logger.LogWarning(
                                    "Server {Server} refused the stored session for character {Character}. The pairing is kept and "
                                    + "will be retried; re-pair only if it stays this way.", _serverAddress, _characterId);
                            SetState(ServerConnectionState.SessionExpired);
                            break;
                        case ServerSessionRefreshOutcome.SessionGone:
                            // Final: the connect loop has already stopped for this, and there is nothing left for
                            // this loop to keep alive either. Stop rather than ask a server twice a minute about a
                            // session it has told us it does not have.
                            _logger.LogWarning(
                                "Server {Server} no longer has the session for character {Character}; the heartbeat stopped. "
                                + "Couple this character to the server again.", _serverAddress, _characterId);
                            SetState(ServerConnectionState.SessionGone);
                            return;
                        // Unavailable: server unreachable — keep the session and try again next tick.
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Backup heartbeat to {Server} (character {Character}) failed.", _serverAddress, _characterId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped (decouple / shutdown).
        }
    }

    // Max idle on the stream before treating the server as gone. The server pushes a keepalive every ~15s,
    // so this tolerates ~3 missed pings — enough to ride out jitter, short enough that a vanished server (a restart
    // behind the tunnel, where transport keepalive can't see the dead origin) is reconnected instead of wedging.
    internal static readonly TimeSpan ReceiveDeadline = TimeSpan.FromSeconds(45);

    private async Task ReadLoopAsync(
        AsyncDuplexStreamingCall<ClientEnvelope, ServerEnvelope> call,
        Action onFirstMessage,
        CancellationToken cancellationToken)
    {
        // Exceptions bubble to ConnectLoopAsync, which reconnects with backoff. A clean end of the stream
        // (server closed it) returns normally → the loop treats it as a disconnect too. The receive-deadline turns
        // a silently half-open stream into a reconnect rather than a wedge in Connected.
        var reader = call.ResponseStream;
        var isFirst = true;
        while (await BusStreamReader.MoveNextWithDeadlineAsync(reader, ReceiveDeadline, cancellationToken))
        {
            if (isFirst)
            {
                // A keepalive counts: anything that arrived proves the whole path — socket, TLS, session, attach —
                // and that is what earns the reconnect counter its reset. Nothing before this point does (ET-95).
                isFirst = false;
                onFirstMessage();
            }

            var server = reader.Current;
            if (BusKeepAlive.IsKeepAlive(server.Event))
                continue; // liveness only — its arrival already reset the deadline

            var characterId = server.Event.CharacterId == 0 ? (int?)null : server.Event.CharacterId;
            var evt = _registry.Deserialize(server.Event.EventType, server.Event.PayloadJson, characterId);
            if (evt is null)
                continue;

            // the source server is known only here (the payload is server-serialized and carries no address).
            // Stamp it so a server-sourced event (e.g. a delivered message) can be answered on the server it came from.
            if (evt is IServerSourcedEvent sourced)
                sourced.SourceServerAddress = _serverAddress;

            // Resolve the bus lazily to avoid a construction cycle (the bus holds the transport).
            var bus = _services.GetRequiredService<IEventBus>();
            await bus.PublishAsync(evt, EventTarget.Local, cancellationToken);
        }
    }
}
