using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// fleet + composition endpoints over the real loopback Kestrel host. With none of the fleet services registered
/// they must degrade gracefully — empty arrays for the lists, 204 for "no active fleet", 404 for an unknown
/// composition — and never 500. The real multi-source data mapping (client-only fleets + per coupled server) is
/// exercised against the live client services by the <c>--localapi-smoke</c> harness, not these stubs.
/// </summary>
public class LocalApiFleetEndpointsTests
{
    [Fact]
    public async Task Fleets_ReturnsEmptyArray_WhenNoFleetServices()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            Assert.Equal("[]", (await GetAsync(port, "/api/v1/fleets")).Trim());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ActiveFleet_Returns204_WhenNotParticipating()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/fleet", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Compositions_ReturnsEmptyArray_WhenNoFleetServices()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            Assert.Equal("[]", (await GetAsync(port, "/api/v1/compositions")).Trim());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task CompositionDetail_Returns404_WhenIdUnknown()
    {
        var server = NewEmptyServer();
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            var response = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/compositions/123", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static LocalApiServer NewEmptyServer()
    {
        // No fleet services registered: every fleet query must early-out to its empty/null shape rather than throwing.
        var provider = new ServiceCollection().BuildServiceProvider();
        return new LocalApiServer(new NoSettings(), provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalApiServer>.Instance);
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

    private sealed class NoSettings : EveUtils.Shared.Modules.Settings.Repositories.ISettingRepository
    {
        public Task<IReadOnlyList<EveUtils.Shared.Modules.Settings.Entities.ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EveUtils.Shared.Modules.Settings.Entities.ClientSetting>>([]);
        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
