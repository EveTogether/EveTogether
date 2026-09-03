using System.Net;
using System.Text.Json;
using EveUtils.Server.Api;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;
using EveUtils.Shared.Modules.ApiKeys.Services;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using EveUtils.Shared.Modules.Gamelog.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-140: fits, characters and metrics answered over the real HTTP route rather than off the bridge. The gate
/// lives on the <c>/api/v1</c> group, so a call that reaches <see cref="ServerApiQueries"/> directly walks past
/// the very thing this milestone has to prove — the key, and the shape the key gets back.
/// </summary>
public class ServerApiHttpTests : IAsyncLifetime
{
    private readonly SqliteServerDbContextFactory _factory = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _key = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The three resources this milestone adds, as they hang under <c>/api/v1</c>.</summary>
    private const string Fits = "/api/v1/fits";
    private const string Characters = "/api/v1/characters";
    private const string Metrics = "/api/v1/metrics";

    // --- The lock ---

    /// <summary>
    /// Each resource answers a key and refuses a caller without one. Driven over the wire because the 401 is
    /// produced by the pipeline, not by the handler: an assertion against the bridge would pass either way.
    /// </summary>
    [Theory]
    [InlineData(Fits)]
    [InlineData(Characters)]
    [InlineData(Metrics)]
    public async Task EachResource_AnswersAValidKey_AndRefusesACallerWithout(string path)
    {
        using HttpResponseMessage withKey = await _GetAsync(path, _key);
        using HttpResponseMessage without = await _GetAsync(path, key: null);

        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
    }

    // --- The shape ---

    /// <summary>
    /// The answer's fields, spelled out. Pinning the whole shape rather than the absence of a name we thought of
    /// is the point: a column added to a DTO later turns this red instead of riding along to an external consumer.
    /// </summary>
    [Theory]
    [InlineData(Fits, "id,esiFittingId,name,shipTypeId,rawJson,sharedByCharacterName,sharedByCharacterId,sharedAt")]
    [InlineData(Characters, "id,name")]
    [InlineData(Metrics, "characterId,characterName,bountyTotal,kills,minedJson")]
    public async Task EachResource_AnswersWithExactlyTheseFields(string path, string expected)
    {
        using HttpResponseMessage response = await _GetAsync(path, _key);
        response.EnsureSuccessStatusCode();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement row = Assert.Single(body.RootElement.EnumerateArray().ToList());

        Assert.Equal(expected.Split(','), row.EnumerateObject().Select(field => field.Name));
    }

    // --- The contract ---

    /// <summary>
    /// The three resources are in the public document, and the reference that renders it is reachable without a
    /// key — both keyless by ratified decision 4, so a consumer can read the contract before it has one.
    /// </summary>
    [Fact]
    public async Task TheThreeResources_AreInThePublicContract_WhichScalarServesWithoutAKey()
    {
        using HttpResponseMessage document = await _GetAsync("/openapi/v1.json", key: null);
        using HttpResponseMessage scalar = await _GetAsync("/scalar", key: null);

        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scalar.StatusCode);

        using JsonDocument contract = JsonDocument.Parse(await document.Content.ReadAsStringAsync(Ct));
        JsonElement paths = contract.RootElement.GetProperty("paths");
        foreach (string path in new[] { Fits, Characters, Metrics })
            Assert.True(paths.TryGetProperty(path, out _), $"{path} is not in the published contract.");
    }

    // --- Host ---

    public async ValueTask InitializeAsync()
    {
        var fits = new SharedFitRepository(_factory);
        var serverAuth = new ServerAuthRepository(_factory);
        var metrics = new CharacterMetricStateRepository(_factory);
        var keys = new ApiKeyRepository(_factory);

        await fits.AddAsync(new SharedFit
        {
            EsiFittingId = 7,
            Name = "Vindicator",
            ShipTypeId = 17_740,
            RawJson = "{\"ship_type_id\":17740}",
            SharedByCharacterId = 90_000_001,
            SharedByCharacterName = "Rin",
            SharedAt = DateTimeOffset.UnixEpoch
        }, Ct);
        await serverAuth.UpsertSyncedAsync(90_000_001, "Rin", new EncryptedToken([1], [2], [3]), cancellationToken: Ct);
        await metrics.UpsertAsync("Rin", 100, 2, "{\"Veldspar\":7}", Ct);
        _key = await _StoreKeyAsync(keys);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IFleetRepository>(new FleetRepository(_factory));
        builder.Services.AddSingleton<IFleetCompositionRepository>(new FleetCompositionRepository(_factory));
        builder.Services.AddSingleton<ISharedFitRepository>(fits);
        builder.Services.AddSingleton<IServerAuthRepository>(serverAuth);
        builder.Services.AddSingleton<ICharacterMetricStateRepository>(metrics);
        builder.Services.AddSingleton<IApiKeyRepository>(keys);
        builder.Services.AddScoped<ServerApiQueries>();

        builder.Services.AddAuthentication(ApiKeyAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.Scheme, null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(ApiKeyAuthentication.Policy, ApiKeyAuthentication.BuildPolicy()));
        builder.Services.AddServerApiDocs();

        _app = builder.Build();
        // Port 0: the OS hands out a free one, so a parallel run has nothing to collide with.
        _app.Urls.Add("http://127.0.0.1:0");
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapServerApi();

        await _app.StartAsync(Ct);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _factory.Dispose();
    }

    private Task<HttpResponseMessage> _GetAsync(string path, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
            request.Headers.Add(ApiKeyAuthentication.HeaderName, key);
        return _client.SendAsync(request, Ct);
    }

    /// <summary>A key with no owner: admin scope over all server data (ratified decision 3).</summary>
    private static async Task<string> _StoreKeyAsync(IApiKeyRepository keys)
    {
        GeneratedApiKey generated = ApiKeySecurity.Generate();
        await keys.AddAsync(new ApiKey
        {
            Label = "tests",
            Prefix = generated.Prefix,
            SecretHash = ApiKeySecurity.Hash(generated.Secret),
            Scopes = ApiKeyScopes.ReadAll,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tests"
        }, Ct);
        return generated.PlainText;
    }
}
