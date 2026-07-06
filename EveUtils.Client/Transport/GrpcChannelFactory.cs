using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Transport;

/// <summary>
/// Builds gRPC channels with TOFU certificate pinning. During pairing the client
/// connects once (accept-any, capture fingerprint) and pins it; afterwards every channel only trusts
/// that exact self-signed cert — no CA, no domain.
///
/// Pinned channels are <b>cached and reused</b> per (address, fingerprint): gRPC channels are thread-safe and
/// meant to be long-lived. The former per-RPC <c>new SocketsHttpHandler</c>/<c>GrpcChannel</c> leaked the handler's
/// connection pool — on Windows (tighter ephemeral-port/socket limits than Linux) that exhausted sockets over a
/// session, so later RPCs failed intermittently and the fleet list silently went empty. One channel per server is
/// built once and shared. A re-pair yields a new fingerprint → a new channel under a new key; the stale one is
/// dropped at shutdown.
///
/// Because the channel lives for the whole session, a silently-dropped HTTP/2 connection would otherwise sit dead in
/// the pool until a client restart ("Error connecting to subchannel" on every RPC). The handler is made resilient
/// (HTTP/2 keepalive, sub-tunnel-idle connection recycling, retry on <c>Unavailable</c>) so a dead connection is
/// detected and replaced instead — see <see cref="_BuildResilientHandler"/>.
/// </summary>
public sealed class GrpcChannelFactory(IServerTrustStore trustStore) : ISingletonService, IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _pinnedChannels = new();

    public PinningChannel CreateForPairing(string address)
    {
        string? presented = null;
        var handler = _BuildResilientHandler((_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;
            presented = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return true; // TOFU: accept on first contact; caller pins `presented`.
        });

        var channel = GrpcChannel.ForAddress(address, _BuildChannelOptions(handler));
        return new PinningChannel(channel, () => presented);
    }

    public GrpcChannel CreatePinned(string address)
    {
        var pinned = trustStore.GetFingerprint(address)
            ?? throw new InvalidOperationException($"No pinned fingerprint for {address}; pair first.");

        return _pinnedChannels.GetOrAdd($"{address}\n{pinned}", _ => BuildPinned(address, pinned));
    }

    /// <summary>
    /// Drops the cached channel(s) for a server so the next <see cref="CreatePinned"/> builds a fresh one. Used by the
    /// bus reconnect loop when a server restart leaves the long-lived channel wedged on a dead HTTP/2 connection that
    /// the handler's keepalive/recycle can't recover (notably through the Cloudflare tunnel) — reusing it would retry
    /// the dead connection forever, which only a client restart cleared. Disposing cancels any in-flight calls on it, so
    /// the connections riding it fail their current attempt and re-attach on the new channel.
    /// </summary>
    public void Invalidate(string address)
    {
        foreach (var key in _pinnedChannels.Keys.Where(k => k.StartsWith($"{address}\n", StringComparison.Ordinal)).ToList())
            if (_pinnedChannels.TryRemove(key, out var channel))
                channel.Dispose();
    }

    private static GrpcChannel BuildPinned(string address, string pinned)
    {
        var handler = _BuildResilientHandler((_, certificate, _, _) =>
            certificate is not null
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                pinned,
                StringComparison.OrdinalIgnoreCase));

        return GrpcChannel.ForAddress(address, _BuildChannelOptions(handler));
    }

    // Keepalive pings detect a connection that died mid-session; the sub-60s idle recycle closes a connection before
    // cloudflared kills the idle stream (~60s), which is the root cause of the stale subchannel — and it does so without
    // depending on the server tolerating pings (a server that rejects them would GOAWAY 'too_many_pings'). The lifetime
    // cap is a final backstop, and EnableMultipleHttp2Connections lets a new call open a fresh connection rather than
    // block on a dead one.
    private static SocketsHttpHandler _BuildResilientHandler(RemoteCertificateValidationCallback validateCertificate) =>
        new()
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validateCertificate },
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(55),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true
        };

    // Retry any RPC that fails with Unavailable (the transient/dead-connection status) so a call that lands on a
    // just-died subchannel recovers on a fresh connection instead of surfacing as a hard error.
    private static GrpcChannelOptions _BuildChannelOptions(SocketsHttpHandler handler) =>
        new()
        {
            HttpHandler = handler,
            ServiceConfig = new ServiceConfig
            {
                MethodConfigs =
                {
                    new MethodConfig
                    {
                        Names = { MethodName.Default },
                        RetryPolicy = new RetryPolicy
                        {
                            MaxAttempts = 5,
                            InitialBackoff = TimeSpan.FromSeconds(1),
                            MaxBackoff = TimeSpan.FromSeconds(5),
                            BackoffMultiplier = 1.5,
                            RetryableStatusCodes = { StatusCode.Unavailable }
                        }
                    }
                }
            }
        };

    public void Dispose()
    {
        foreach (var channel in _pinnedChannels.Values)
            channel.Dispose();
        _pinnedChannels.Clear();
    }
}
