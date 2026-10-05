using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Gamelog;
using EveUtils.Client.LocalApi;
using EveUtils.Client.Opsec;
using EveUtils.Client.Platform;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// What the Local API hands out about where you are ("Include my location", OPSEC) and who may read it at all (the
/// origin guard on HTTP and the WebSocket).
/// </summary>
public class LocalApiPrivacyTests
{
    private const string Pilot = "Privacy Pilot";

    [Fact]
    public async Task Metrics_IncludeLocationOff_LocationIsNull()
    {
        var metrics = await _MetricsInJitaAsync(includeLocation: false, opsec: false);

        Assert.Null(metrics.Location);
    }

    [Fact]
    public async Task Metrics_IncludeLocationOn_LocationIsSent()
    {
        var metrics = await _MetricsInJitaAsync(includeLocation: true, opsec: false);

        Assert.Equal("Jita", metrics.Location);
    }

    [Fact]
    public async Task Metrics_IncludeLocationOnWithOpsec_LocationIsNull()
    {
        var metrics = await _MetricsInJitaAsync(includeLocation: true, opsec: true);

        Assert.Null(metrics.Location);
    }

    [Fact]
    public async Task FleetMetrics_IncludeLocationOff_LocationSampleIsNotStreamed()
    {
        var bus = new InProcessEventBus();
        var provider = new ServiceCollection().AddSingleton<IEventBus>(bus).BuildServiceProvider();
        var server = _NewServer(provider);
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);
            await _ReceiveTextAsync(client, TestContext.Current.CancellationToken); // snapshot

            await bus.PublishAsync(new FleetMetricEvent(new MetricSample(1, 7, MetricKind.Location, 30000142, 1, "Jita")),
                cancellationToken: TestContext.Current.CancellationToken);
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample(1, 7, MetricKind.Dps, 250, 2)),
                cancellationToken: TestContext.Current.CancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var sample = await _NextFleetSampleAsync(client, cts.Token);
            Assert.Equal("Dps", sample.GetProperty("kind").GetString());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Http_ForeignOrigin_IsRefusedWithoutCorsHeader()
    {
        var server = _NewServer(new ServiceCollection().BuildServiceProvider());
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();

            var foreign = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            foreign.Headers.Add("Origin", "http://evil.test");
            var refused = await http.SendAsync(foreign, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));

            var sameOrigin = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            sameOrigin.Headers.Add("Origin", $"http://127.0.0.1:{port}");
            var served = await http.SendAsync(sameOrigin, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task WebSocket_ForeignOrigin_IsRefused()
    {
        var server = _NewServer(new ServiceCollection().BuildServiceProvider());
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);

            using var foreign = new ClientWebSocket();
            foreign.Options.SetRequestHeader("Origin", "http://evil.test");
            await Assert.ThrowsAsync<WebSocketException>(() =>
                foreign.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken));

            using var sameOrigin = new ClientWebSocket();
            sameOrigin.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{port}");
            await sameOrigin.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketState.Open, sameOrigin.State);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task WebSocket_AllowlistedOrigin_IsAccepted()
    {
        var server = _NewServer(new ServiceCollection().BuildServiceProvider(), allowedOrigins: "http://overlay.test");
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);

            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", "http://overlay.test");
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketState.Open, client.State);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static async Task<EveUtils.Client.LocalApi.Dtos.CharacterMetricsDto> _MetricsInJitaAsync(bool includeLocation, bool opsec)
    {
        var bus = new InProcessEventBus();
        var services = new ServiceCollection();
        var presence = new EveClientPresenceService(NullLogger<EveClientPresenceService>.Instance, new RunningProbe(Pilot));
        presence.PollOnce();
        services.AddSingleton(presence);
        services.AddSingleton<IOpsecService>(new FakeOpsecService { IsEnabled = opsec });
        services.AddSingleton(provider => new GamelogClientService(provider, bus));
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<GamelogClientService>().SetLocation(Pilot, "Jita", DateTime.UtcNow);

        var queries = new LocalApiQueries(provider, new LocalApiPrivacy(provider, includeLocation));
        var metrics = await queries.GetMetricsAsync(TestContext.Current.CancellationToken);
        return Assert.Single(metrics);
    }

    private static async Task<JsonElement> _NextFleetSampleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var envelope = JsonDocument.Parse(await _ReceiveTextAsync(socket, cancellationToken));
            if (envelope.RootElement.GetProperty("type").GetString() == "fleet.metrics")
                return envelope.RootElement.GetProperty("data").Clone();
        }
    }

    private static LocalApiServer _NewServer(IServiceProvider provider, string? allowedOrigins = null) =>
        new(new StubSettings(allowedOrigins), provider, NullLogger<LocalApiServer>.Instance);

    private static async Task<string> _ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, cancellationToken);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
        }
        while (!received.EndOfMessage);
        return builder.ToString();
    }

    private static int _FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RunningProbe(string characterName) : IEveClientProbe
    {
        public EveClientEvidence Probe() => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { characterName }, new HashSet<int>());
        public int RunningClientCount() => 1;
        public bool Activate(string name) => false;
    }

    private sealed class StubSettings(string? allowedOrigins) : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ClientSetting> rows = allowedOrigins is null
                ? []
                : [new ClientSetting { Key = LocalApiServer.AllowedOriginsSettingKey, Value = allowedOrigins }];
            return Task.FromResult(rows);
        }

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
