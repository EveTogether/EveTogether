using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// REST endpoints over the real loopback Kestrel host: characters/fits/metrics map to the public DTOs (no
/// tokens), missing fit → 404, and the OpenAPI document + Scalar UI are reachable. Uses a minimal stub provider
/// (no real DB/SDE) so the test is fast and isolated — it never touches the real data directory.
/// </summary>
public class LocalApiEndpointsTests
{
    private const string FitRawJson =
        """{"fitting_id":1,"name":"Test Rifter","description":"d","ship_type_id":587,"items":[{"type_id":2873,"flag":"LoSlot0","quantity":1}]}""";

    [Fact]
    public async Task Characters_ReturnsCoupledChars_WithPortraitUrl_AndNoSecrets()
    {
        var server = NewServer(out _);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            var body = await GetAsync(port, "/api/v1/characters");

            Assert.Contains("Test Pilot", body);
            Assert.Contains("https://images.evetech.net/characters/95465499/portrait", body);
            // Identity only — never tokens or granted scopes.
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grantedScopes", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Fits_ListsSeededFit_WithResolvedShipNameFallback()
    {
        var server = NewServer(out _);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            var body = await GetAsync(port, "/api/v1/fits");

            Assert.Contains("Test Rifter", body);
            Assert.Contains("\"shipTypeId\":587", body);
            Assert.Contains("type 587", body); // fallback name resolver (no SDE in the stub)
            Assert.Contains("\"scope\":\"local\"", body); // local fits carry their source; no server services here so no server rows
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task FitDetail_ReturnsItems_And404ForMissingFit()
    {
        var server = NewServer(out var fitId);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);

            var detail = await GetAsync(port, $"/api/v1/fits/{fitId}");
            Assert.Contains("Test Rifter", detail);
            Assert.Contains("\"typeId\":2873", detail); // the fitted item is mapped

            using var http = new HttpClient();
            var missing = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/fits/999999", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Metrics_ReturnsEmptyArray_WhenNoGamelogService()
    {
        var server = NewServer(out _);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            var body = await GetAsync(port, "/api/v1/metrics");
            Assert.Equal("[]", body.Trim());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task OpenApiDocument_AndScalarUi_AreReachable()
    {
        var server = NewServer(out _);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);

            var openapi = await GetAsync(port, "/openapi/v1.json");
            Assert.Contains("openapi", openapi);
            Assert.Contains("/api/v1/fits", openapi);

            using var http = new HttpClient();
            var scalar = await http.GetAsync($"http://127.0.0.1:{port}/scalar/", TestContext.Current.CancellationToken);
            Assert.True(scalar.IsSuccessStatusCode, $"Scalar UI not reachable: {(int)scalar.StatusCode}");
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Index_And_Docs_AreServed_WithBaseUrlTemplated()
    {
        var server = NewServer(out _);
        var port = FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);

            var index = await GetAsync(port, "/");
            Assert.Contains("/scalar", index);   // link to the OpenAPI reference
            Assert.Contains("/docs", index);      // link to the connect guide
            Assert.Contains($"http://127.0.0.1:{port}", index); // {{BASE_URL}} templated in

            var docs = await GetAsync(port, "/docs");
            Assert.Contains("WebSocket", docs);
            Assert.Contains("SignalR", docs);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static LocalApiServer NewServer(out int seededFitId)
    {
        var fit = new LocalFitting { Id = 42, Name = "Test Rifter", ShipTypeId = 587, RawJson = FitRawJson, Description = "d" };
        seededFitId = fit.Id;

        var repo = new FakeFittingRepository([fit]);
        var registry = new StubCharacterRegistry([new Character("Test Pilot", 95465499)]);

        var services = new ServiceCollection();
        services.AddSingleton<ICharacterRegistry>(registry);
        services.AddScoped<IFittingRepository>(_ => repo);
        var provider = services.BuildServiceProvider();

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

    private sealed class StubCharacterRegistry(IReadOnlyList<Character> characters) : ICharacterRegistry
    {
        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(characters);
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event Action RegistryChanged { add { } remove { } }
    }

    private sealed class FakeFittingRepository(List<LocalFitting> fits) : IFittingRepository
    {
        public Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalFitting>>(fits);
        public Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(fits.FirstOrDefault(f => f.Id == id));

        public Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task BackfillContentHashesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
