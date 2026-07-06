using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using EveUtils.Shared.Modules.Settings.Entities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The local widget API surface over the real loopback host: the SignalR negotiate handshake answers, the type resolver returns a
/// name + icon URL, the host-header guard rejects a foreign Host (anti-DNS-rebinding), and the optional API key gates
/// <c>/api</c> while leaving the docs open. No client services are registered — these prove the wiring, not the data.
/// </summary>
public class LocalApiHardeningTests
{
    [Fact]
    public async Task Types_ResolvesTypeId_WithIconUrl()
    {
        var server = NewServer(apiKey: null);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            var body = await GetAsync(port, "/api/v1/types/587");
            Assert.Contains("\"id\":587", body);
            Assert.Contains("type 587", body); // fallback name resolver (no SDE in the stub)
            Assert.Contains("https://images.evetech.net/types/587/icon", body);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task SignalRHub_Negotiate_Responds()
    {
        var server = NewServer(apiKey: null);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            var response = await http.PostAsync($"http://127.0.0.1:{port}/hub/fleet/negotiate?negotiateVersion=1",
                content: null, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"negotiate status {(int)response.StatusCode}");
            Assert.Contains("connectionId", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task SignalRHub_Connect_ReceivesSnapshot()
    {
        var server = NewServer(apiKey: null);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            await using var connection = new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:{port}/hub/fleet")
                .Build();

            var snapshot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.On<JsonElement>("snapshot", data => snapshot.TrySetResult(data));
            await connection.StartAsync(TestContext.Current.CancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var data = await snapshot.Task.WaitAsync(cts.Token);
            Assert.True(data.TryGetProperty("metrics", out var metrics)); // the snapshot DTO arrived, camelCase
            Assert.Equal(JsonValueKind.Array, metrics.ValueKind);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task HostHeader_Foreign_IsRejected()
    {
        var server = NewServer(apiKey: null);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            request.Headers.Host = "evil.example"; // connects to loopback but claims another host → rebinding attempt
            var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode); // 421
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ApiKey_WhenSet_GuardsApiButNotDocs()
    {
        var server = NewServer(apiKey: "s3cret-key");
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();

            var noKey = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/health", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

            var withHeader = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            withHeader.Headers.Add("X-Api-Key", "s3cret-key");
            var ok = await http.SendAsync(withHeader, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // The docs/landing pages are not behind the key.
            var docs = await http.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Cors_Allowlist_RestrictsToConfiguredOrigins()
    {
        var server = NewServer(allowedOrigins: "http://allowed.test");
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();

            var allowed = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            allowed.Headers.Add("Origin", "http://allowed.test");
            var allowedResponse = await http.SendAsync(allowed, TestContext.Current.CancellationToken);
            Assert.Equal("http://allowed.test", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

            var denied = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/health");
            denied.Headers.Add("Origin", "http://evil.test");
            var deniedResponse = await http.SendAsync(denied, TestContext.Current.CancellationToken);
            Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin")); // not in the allowlist
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static LocalApiServer NewServer(string? apiKey = null, string? allowedOrigins = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new LocalApiServer(new StubSettings(apiKey, allowedOrigins), provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalApiServer>.Instance);
    }

    private static async Task<string> GetAsync(int port, string path)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubSettings(string? apiKey = null, string? allowedOrigins = null) : EveUtils.Shared.Modules.Settings.Repositories.ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default)
        {
            var rows = new List<ClientSetting>();
            if (apiKey is not null) rows.Add(new ClientSetting { Key = LocalApiServer.ApiKeySettingKey, Value = apiKey });
            if (allowedOrigins is not null) rows.Add(new ClientSetting { Key = LocalApiServer.AllowedOriginsSettingKey, Value = allowedOrigins });
            return Task.FromResult<IReadOnlyList<ClientSetting>>(rows);
        }

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
