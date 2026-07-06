using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The opt-in local widget API host: disabled by default, binds 127.0.0.1 only (never an external
/// interface), serves <c>/api/v1/health</c>, survives a port conflict without crashing, and rebinds on change.
/// Exercises the real Kestrel host over loopback — no UI involved.
/// </summary>
public class LocalApiServerTests
{
    [Fact]
    public async Task ApplyAsync_Disabled_DoesNotBind()
    {
        var port = FreePort();
        var server = NewServer();

        await server.ApplyAsync(enabled: false, port, TestContext.Current.CancellationToken);

        Assert.Equal(LocalApiStatus.Stopped, server.Status.Status);
        Assert.True(IsBindable(port), "no listener should occupy the port when the API is disabled");
    }

    [Fact]
    public async Task ApplyAsync_Enabled_HealthRespondsOnLoopback()
    {
        var port = FreePort();
        var server = NewServer();
        try
        {
            await server.ApplyAsync(enabled: true, port, TestContext.Current.CancellationToken);

            Assert.Equal(LocalApiStatus.Running, server.Status.Status);
            Assert.Equal($"http://127.0.0.1:{port}", server.Status.Url);

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/health", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("\"status\":\"ok\"", body);          // camelCase contract
            Assert.Contains($"\"apiVersion\":\"{LocalApiServer.ApiVersion}\"", body);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ApplyAsync_Enabled_BindsLoopbackOnly()
    {
        var port = FreePort();
        var server = NewServer();
        try
        {
            await server.ApplyAsync(enabled: true, port, TestContext.Current.CancellationToken);

            Assert.NotNull(server.Status.BoundAddresses);
            Assert.NotEmpty(server.Status.BoundAddresses!);
            // Every bound address must be loopback — never 0.0.0.0 or [::] (which would expose it to the network).
            foreach (var address in server.Status.BoundAddresses!)
                Assert.Contains("127.0.0.1", address);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ApplyAsync_PortInUse_ReportsStatus_WithoutCrashing()
    {
        var port = FreePort();
        // Occupy the port first so Kestrel cannot bind it.
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        var server = NewServer();
        try
        {
            await server.ApplyAsync(enabled: true, port, TestContext.Current.CancellationToken);

            Assert.Equal(LocalApiStatus.PortInUse, server.Status.Status);
            Assert.NotNull(server.Status.Message);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
            blocker.Stop();
        }
    }

    [Fact]
    public async Task ApplyAsync_Rebind_FreesOldPortAndServesNew()
    {
        var portA = FreePort();
        var portB = FreePort();
        var server = NewServer();
        try
        {
            await server.ApplyAsync(enabled: true, portA, TestContext.Current.CancellationToken);
            Assert.Equal(LocalApiStatus.Running, server.Status.Status);

            await server.ApplyAsync(enabled: true, portB, TestContext.Current.CancellationToken);

            Assert.Equal(portB, server.Status.Port);
            Assert.True(IsBindable(portA), "the old port must be released after a rebind");

            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{portB}/api/v1/health", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StartAsync_StartsWhenSettingEnabled_StaysStoppedWhenOff()
    {
        var port = FreePort();

        // Default off: no enabled setting → host stays stopped.
        var off = NewServer(new InMemorySettings());
        await off.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LocalApiStatus.Stopped, off.Status.Status);

        // Enabled via settings → host starts on the configured port.
        var on = NewServer(new InMemorySettings(
            (LocalApiServer.EnabledSettingKey, "true"),
            (LocalApiServer.PortSettingKey, port.ToString())));
        try
        {
            await on.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(LocalApiStatus.Running, on.Status.Status);
            Assert.Equal(port, on.Status.Port);
        }
        finally
        {
            await on.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static LocalApiServer NewServer(ISettingRepository? settings = null) =>
        new(settings ?? new InMemorySettings(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<LocalApiServer>.Instance);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsBindable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed class InMemorySettings(params (string Key, string Value)[] entries) : ISettingRepository
    {
        private readonly List<ClientSetting> _settings =
            [.. System.Linq.Enumerable.Select(entries, e => new ClientSetting { Key = e.Key, Value = e.Value })];

        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSetting>>(_settings);

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _settings.RemoveAll(s => s.Key == key);
            _settings.Add(new ClientSetting { Key = key, Value = value });
            return Task.CompletedTask;
        }
    }
}
