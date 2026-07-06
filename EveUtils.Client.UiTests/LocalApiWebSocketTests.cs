using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// realtime WebSocket endpoint over the real loopback Kestrel host. A connecting client must receive a
/// <c>snapshot</c> envelope immediately, then the steady <c>metrics</c> tick — even with no client services
/// registered (the own-metrics list is then empty, but the stream still flows). The fleet event forwarding is
/// exercised against the live bus by the <c>--localapi-smoke</c> harness, not these stubs.
/// </summary>
public class LocalApiWebSocketTests
{
    [Fact]
    public async Task WebSocket_OnConnect_SendsSnapshotEnvelope()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(await ReceiveTextAsync(client, TestContext.Current.CancellationToken));
            Assert.Equal("snapshot", doc.RootElement.GetProperty("type").GetString());
            Assert.True(doc.RootElement.GetProperty("data").TryGetProperty("metrics", out var metrics));
            Assert.Equal(JsonValueKind.Array, metrics.ValueKind); // no gamelog service → empty, but present
            Assert.True(doc.RootElement.TryGetProperty("ts", out _));
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task WebSocket_Streams_MetricsTick_AfterSnapshot()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);

            using var snapshot = JsonDocument.Parse(await ReceiveTextAsync(client, TestContext.Current.CancellationToken));
            Assert.Equal("snapshot", snapshot.RootElement.GetProperty("type").GetString());

            // The 1 Hz own-metrics tick follows; allow a couple of seconds for the next envelope.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var tick = JsonDocument.Parse(await ReceiveTextAsync(client, cts.Token));
            Assert.Equal("metrics", tick.RootElement.GetProperty("type").GetString());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static LocalApiServer NewEmptyServer()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new LocalApiServer(new NoSettings(), provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalApiServer>.Instance);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);
        return builder.ToString();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class NoSettings : EveUtils.Shared.Modules.Settings.Repositories.ISettingRepository
    {
        public Task<IReadOnlyList<EveUtils.Shared.Modules.Settings.Entities.ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EveUtils.Shared.Modules.Settings.Entities.ClientSetting>>([]);
        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
