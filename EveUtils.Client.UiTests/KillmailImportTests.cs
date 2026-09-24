using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EveUtils.Client.Esi.Testing;
using EveUtils.Client.Killmails;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EveUtils.Client.Esi.Testing.EsiTestResponses;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The killmail import end to end over the real pivot and cache handler, with scripted ESI routes and the real
/// SQLite store of a throwaway client instance.
/// </summary>
public sealed class KillmailImportTests : IDisposable
{
    private const int CharacterId = 77;
    private const string Page1 = "/characters/77/killmails/recent/?page=1";
    private const string Page2 = "/characters/77/killmails/recent/?page=2";
    private const string Page3 = "/characters/77/killmails/recent/?page=3";

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "esi-killmail-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestClientInstance _instance = TestClientInstance.Create();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

    private ILocalKillmailRepository Repository => _instance.Services.GetRequiredService<ILocalKillmailRepository>();
    private IServiceScopeFactory Scopes => _instance.Services.GetRequiredService<IServiceScopeFactory>();

    [Theory]
    [InlineData("/characters/77/killmails/recent/", false)]
    [InlineData("/killmails/1/abc/", true)]
    public async Task Cache_OnlyTheKillmailItselfIsKeptForever(string path, bool expectedForever)
    {
        _routes[path] = () => Json(200, "[]").WithExpires(TimeSpan.FromMinutes(5));
        var (client, store, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await client.GetAsync<object>(path, cancellationToken: TestContext.Current.CancellationToken);

        var entry = await store.GetAsync(FileEsiCacheStore.KeyFor(EsiEndpoints.PublicDataBaseUrl + path), TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(expectedForever, entry.ExpiresAt is null);
    }

    [Fact]
    public async Task ImportAsync_StopsAtTheFirstFullyKnownPage_AndCallsOnlyTheKillmailEndpoints()
    {
        await Repository.AddMissingAsync(CharacterId, [_Stored(2)], TestContext.Current.CancellationToken);
        _routes[Page1] = () => _RecentPage(3, 1);
        _routes[Page2] = () => _RecentPage(3, 2);
        _routes[Page3] = () => _RecentPage(3, 4);
        _routes["/killmails/1/hash1/"] = () => Json(200, _Killmail(1));
        var (client, _, stub) = _Pipeline(EsiAuthorization.Authorized("token"));

        var result = await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal([Page1, Page2, "/killmails/1/hash1/"], stub.Captured.Select(request => new Uri(request.Uri).PathAndQuery));
    }

    [Fact]
    public async Task ImportAsync_KeepsAMailThatLeftTheRecentList()
    {
        await Repository.AddMissingAsync(CharacterId, [_Stored(2)], TestContext.Current.CancellationToken);
        _routes[Page1] = () => _RecentPage(1, 1);
        _routes["/killmails/1/hash1/"] = () => Json(200, _Killmail(1));
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        var stored = await Repository.GetForCharacterAsync(CharacterId, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], stored.Select(killmail => killmail.KillmailId).Order());
    }

    [Theory]
    [InlineData(77, true)]   // this character died to pilot 88's final blow
    [InlineData(99, false)]  // this character helped on a kill pilot 88 finished
    public async Task ImportAsync_IsLossFollowsTheVictim_AndKeepsDamageTaken(int victimId, bool expectedLoss)
    {
        _routes[Page1] = () => _RecentPage(1, 1);
        _routes["/killmails/1/hash1/"] = () => Json(200, $$"""
            {"killmail_id":1,"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":32000042,
             "victim":{"character_id":{{victimId}},"ship_type_id":17715,"damage_taken":12345,"items":[]},
             "attackers":[{"character_id":88,"damage_done":9000,"final_blow":true},
                          {"character_id":77,"damage_done":3345,"final_blow":false}]}
            """);
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        var killmail = Assert.Single(await Repository.GetForCharacterAsync(CharacterId, TestContext.Current.CancellationToken));
        Assert.Equal(expectedLoss, killmail.IsLoss);
        Assert.Equal(12345, killmail.DamageTaken);
    }

    [Fact]
    public async Task ImportAsync_WithoutTheScope_ReportsScopeMissingWithoutCallingEsi()
    {
        _routes[Page1] = () => _RecentPage(1, 1);
        var (client, _, stub) = _Pipeline(EsiAuthorization.ScopeMissing("esi-killmails.read_killmails.v1"));

        var result = await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        Assert.Equal(KillmailImportStatus.ScopeMissing, result.Status);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task ImportAsync_SumsStacksPerFlagTypeAndNesting()
    {
        _routes[Page1] = () => _RecentPage(1, 1);
        _routes["/killmails/1/hash1/"] = () => Json(200, """
            {"killmail_id":1,"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":30000142,
             "victim":{"character_id":77,"ship_type_id":17715,"damage_taken":1,"items":[
               {"item_type_id":3001,"flag":27,"quantity_destroyed":1,"quantity_dropped":1},
               {"item_type_id":240,"flag":5,"quantity_destroyed":100},
               {"item_type_id":240,"flag":5,"quantity_dropped":50},
               {"item_type_id":3467,"flag":5,"quantity_dropped":1,"items":[
                 {"item_type_id":240,"flag":5,"quantity_destroyed":30}]}]},
             "attackers":[{"damage_done":1,"final_blow":true}]}
            """);
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        var killmail = Assert.Single(await Repository.GetForCharacterAsync(CharacterId, TestContext.Current.CancellationToken));
        (int, int, bool, long, long)[] expected =
            [(5, 240, false, 100, 50), (5, 240, true, 30, 0), (5, 3467, false, 0, 1), (27, 3001, false, 1, 1)];
        Assert.Equal(expected, killmail.Items
            .Select(item => (item.Flag, item.TypeId, item.IsNested, item.QuantityDestroyed, item.QuantityDropped))
            .OrderBy(item => item.Flag).ThenBy(item => item.TypeId).ThenBy(item => item.IsNested));
    }

    [Fact]
    public async Task ImportAsync_StoresEveryAttackerInEsiOrder_NpcsIncluded()
    {
        _routes[Page1] = () => _RecentPage(1, 1);
        _routes["/killmails/1/hash1/"] = () => Json(200, """
            {"killmail_id":1,"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":30000142,
             "victim":{"character_id":99,"ship_type_id":587,"damage_taken":6000,"items":[]},
             "attackers":[
               {"character_id":11,"corporation_id":1001,"alliance_id":2001,"ship_type_id":621,"weapon_type_id":2873,"damage_done":1500,"final_blow":false},
               {"character_id":12,"corporation_id":1002,"ship_type_id":622,"weapon_type_id":2874,"damage_done":1400,"final_blow":true},
               {"character_id":77,"corporation_id":1003,"ship_type_id":623,"weapon_type_id":2875,"damage_done":1300,"final_blow":false},
               {"corporation_id":1000125,"faction_id":500010,"ship_type_id":30189,"weapon_type_id":30189,"damage_done":1000,"final_blow":false},
               {"character_id":14,"corporation_id":1004,"ship_type_id":624,"weapon_type_id":2876,"damage_done":500,"final_blow":false},
               {"character_id":15,"corporation_id":1005,"ship_type_id":625,"weapon_type_id":2877,"damage_done":300,"final_blow":false}]}
            """);
        var (client, _, _) = _Pipeline(EsiAuthorization.Authorized("token"));

        await new EsiKillmailImporter(client, Repository, Scopes).ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        var killmail = Assert.Single(await Repository.GetForCharacterAsync(CharacterId, TestContext.Current.CancellationToken));
        (int, int?, int?, int?, int?, int?, int?, int, bool)[] expected =
            [
                (0, 11, 1001, 2001, null, 621, 2873, 1500, false),
                (1, 12, 1002, null, null, 622, 2874, 1400, true),
                (2, 77, 1003, null, null, 623, 2875, 1300, false),
                (3, null, 1000125, null, 500010, 30189, 30189, 1000, false),
                (4, 14, 1004, null, null, 624, 2876, 500, false),
                (5, 15, 1005, null, null, 625, 2877, 300, false)
            ];
        Assert.Equal(expected, killmail.Attackers.Select(attacker => (attacker.Ordinal, attacker.AttackerCharacterId, attacker.CorporationId,
                attacker.AllianceId, attacker.FactionId, attacker.ShipTypeId, attacker.WeaponTypeId, attacker.DamageDone,
                attacker.FinalBlow)));
    }

    [Fact]
    public async Task ImportAsync_ACachedPage1StillLeadsToPage2()
    {
        _routes[Page1] = () => _RecentPage(2, 1).WithExpires(TimeSpan.FromMinutes(5));
        _routes[Page2] = () => Json(500, "{\"error\":\"boom\"}");
        _routes["/killmails/1/hash1/"] = () => Json(200, _Killmail(1));
        _routes["/killmails/2/hash2/"] = () => Json(200, _Killmail(2));
        var (client, _, stub) = _Pipeline(EsiAuthorization.Authorized("token"));
        var importer = new EsiKillmailImporter(client, Repository, Scopes);
        await importer.ImportAsync(CharacterId, TestContext.Current.CancellationToken);
        _routes[Page2] = () => _RecentPage(2, 2);

        var result = await importer.ImportAsync(CharacterId, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ImportedCount);
        Assert.Single(stub.Captured, request => new Uri(request.Uri).PathAndQuery == Page1);
    }

    public void Dispose()
    {
        _instance.Dispose();
        Directory.Delete(_cacheDirectory, recursive: true);
    }

    private (IEsiClient Client, FileEsiCacheStore Store, StubHttpMessageHandler Stub) _Pipeline(EsiAuthorization authorization)
    {
        var stub = new StubHttpMessageHandler((request, _) => _routes[request.RequestUri?.PathAndQuery ?? ""]());
        var store = new FileEsiCacheStore(_cacheDirectory);
        var cache = new EsiCacheHandler(store, new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance)) { InnerHandler = stub };
        var client = new EsiClient(new SingleClientHttpFactory(new HttpClient(cache)), new FakeEsiTokenProvider(authorization),
            new EsiOutageDetector(new EsiAvailabilityState()), NullLogger<EsiClient>.Instance);
        return (client, store, stub);
    }

    private static HttpResponseMessage _RecentPage(int pages, params int[] killmailIds)
    {
        var response = Json(200, "[" + string.Join(",", killmailIds.Select(id => $$"""{"killmail_id":{{id}},"killmail_hash":"hash{{id}}"}""")) + "]");
        response.Headers.TryAddWithoutValidation(EsiCacheHeaders.Pages, pages.ToString());
        return response;
    }

    private static string _Killmail(int killmailId) => $$"""
        {"killmail_id":{{killmailId}},"killmail_time":"2026-09-20T12:00:00Z","solar_system_id":30000142,
         "victim":{"character_id":99,"ship_type_id":587,"damage_taken":1,"items":[]},
         "attackers":[{"character_id":77,"damage_done":1,"final_blow":true}]}
        """;

    private static LocalKillmail _Stored(int killmailId) => new()
    {
        CharacterId = CharacterId,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        SolarSystemId = 30000142,
        VictimShipTypeId = 587
    };
}
