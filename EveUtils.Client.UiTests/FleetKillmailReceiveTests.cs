using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using UiDispatcher = Avalonia.Threading.Dispatcher;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi.Testing;
using EveUtils.Client.Fleet;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EveUtils.Client.Esi.Testing.EsiTestResponses;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-371: a fleet mate's <c>fleet.killmail-share</c> is fetched from the public killmail endpoint, stored under the
/// mate's character with the fleet it came from, linked to the mate's group run and shown in LINKED LOSS — while the
/// own KILLMAILS overview keeps showing own mails only.
/// </summary>
public sealed class FleetKillmailReceiveTests : IDisposable
{
    private const int Own = 95220001;
    private const int OwnAlt = 95220005;
    private const int Mate = 95220002;
    private const int SecondMate = 95220003;
    private const int ThirdMate = 95220004;
    private const int Enemy = 95229999;
    private const long FleetId = 42;
    private const int Jita = 30000142;
    private const int Gila = 17715;
    private static readonly DateTime StartedAtUtc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "esi-fleet-killmail-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Func<int, HttpResponseMessage>> _routes = [];
    private readonly Dictionary<string, int> _hits = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ReceivedShare_FetchesPublicly_StoresUnderTheMate_LinksTheirRun_AndStaysOutOfTheOwnOverview()
    {
        using TestClientInstance instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        ILocalKillmailRepository repository = instance.Services.GetRequiredService<ILocalKillmailRepository>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(
            new Character("Own Pilot", Own, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails]), Ct);
        await repository.AddMissingAsync(Own, [_OwnKill(500)], Ct);
        await repository.AddMissingAsync(OwnAlt, [_OwnKill(501, OwnAlt)], Ct);

        // The mate's group run, synced to this client the way RunSynchronizationService applies it.
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Mate, ActivityKind.Site, StartedAtUtc, 0,
            "Angel Hideaway", Jita), Ct);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), Ct);
        _routes[_Path(1)] = _ => Json(200, _Killmail(1, victim: Mate, attackers: [Enemy]));
        (FleetKillmailShareReceiver receiver, StubHttpMessageHandler stub) = _Receiver(instance);
        using IDisposable subscription = receiver;

        await _ShareAsync(instance, Mate, FleetId, 1, 1);
        await receiver.WhenIdleAsync();

        CapturedRequest request = Assert.Single(stub.Captured);
        Assert.Equal(_Path(1), new Uri(request.Uri).AbsolutePath);
        Assert.Null(request.Authorization);
        LocalKillmail stored = Assert.Single(await repository.GetForCharacterAsync(Mate, Ct));
        Assert.Equal(FleetId, stored.SharedFromFleetId);
        Assert.True(stored.IsLoss);
        Assert.Equal(started.Value, stored.RunId);
        Assert.Equal([500], (await repository.GetForCharacterAsync(Own, Ct)).Select(killmail => killmail.KillmailId));

        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);
        IReadOnlyList<ActivityOverviewRowDto> rows = (await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? [];
        var detail = new ActivityDetailViewModel(dispatcher, rows.Single(row => row.RunId == started.Value).ActivitySummaryId,
            sde: instance.Services.GetRequiredService<ISdeAccessor>());
        await detail.LoadAsync(Ct);
        LinkedLossViewModel loss = Assert.Single(detail.Sections.OfType<LossDetailSectionViewModel>().Single().Losses);
        Assert.Contains("Gila", loss.ShipText);

        // The opposite outcome: the own overview, for "All characters" and for the own character, shows only own mails.
        KillmailsOverviewViewModel overview = new(dispatcher, new RecordingDialogService(), instance.Services,
            [new Character("Own Pilot", Own, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails]),
                new Character("Own Alt", OwnAlt, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails])], (_, _) => Task.CompletedTask);
        await overview.LoadAsync(Ct);
        Assert.True(await _WaitForAsync(() => !overview.IsBusy));
        Assert.Equal([500, 501], _VisibleIds(overview).Order());
        overview.SelectedCharacter = overview.CharacterOptions.Single(option => option.CharacterId == Own);
        Assert.True(await _WaitForAsync(() => !overview.IsBusy));
        Assert.Equal([500], _VisibleIds(overview));
        overview.Dispose();

        // A hash that is not a killmail hash never reaches the ESI path (a mate's input, ET-371 trust boundary).
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetKillmailShareEvent(new FleetKillmailShare
        {
            FleetId = 99,
            UnixMs = 1,
            Killmails = [new FleetKillmailReference { KillmailId = 9, Hash = "../../status", KillmailTimeUtc = StartedAtUtc }],
        }, Mate), EventTarget.Local, Ct);
        await receiver.WhenIdleAsync();
        Assert.Single(stub.Captured);
        Assert.Equal([1], (await repository.GetForCharacterAsync(Mate, Ct)).Select(killmail => killmail.KillmailId));

        // Withdrawn by a newer empty share: the row goes, and the run's stored totals no longer carry the loss.
        await _ShareAsync(instance, Mate, FleetId, 2);
        await receiver.WhenIdleAsync();
        Assert.Empty(await repository.GetForCharacterAsync(Mate, Ct));
        var afterWithdrawal = new ActivityDetailViewModel(dispatcher, rows.Single(row => row.RunId == started.Value).ActivitySummaryId,
            sde: instance.Services.GetRequiredService<ISdeAccessor>());
        await afterWithdrawal.LoadAsync(Ct);
        Assert.DoesNotContain(afterWithdrawal.Sections.OfType<LossDetailSectionViewModel>(), section => section.HasContent);
    }

    [Fact]
    public async Task SameKillFromThreeMates_WithAFirstFailingFetch_StoresNothingHalf_ThenOneGetAndThreeOwners()
    {
        using TestClientInstance instance = _Instance();
        ILocalKillmailRepository repository = instance.Services.GetRequiredService<ILocalKillmailRepository>();
        _routes[_Path(8)] = _ => Json(200, _Killmail(8, victim: Enemy, attackers: [Mate]));
        // The shared kill: ESI answers 404 once (not yet published), then the mail.
        _routes[_Path(7)] = call => call == 1
            ? Json(404, """{"error":"Killmail not found"}""")
            : Json(200, _Killmail(7, victim: Enemy, attackers: [Mate, SecondMate, ThirdMate]));
        (FleetKillmailShareReceiver receiver, _) = _Receiver(instance);
        using IDisposable subscription = receiver;

        // 8 fetches fine, 7 fails: nothing of this share is stored, not even 8.
        await _ShareAsync(instance, Mate, FleetId, 1, 8, 7);
        await receiver.WhenIdleAsync();
        Assert.Empty(await repository.GetForCharacterAsync(Mate, Ct));

        // The next share retries; the other two mates' shares of the same kill come from the cache.
        await _ShareAsync(instance, Mate, FleetId, 2, 8, 7);
        await _ShareAsync(instance, SecondMate, FleetId, 1, 7);
        await _ShareAsync(instance, ThirdMate, FleetId, 1, 7);
        await receiver.WhenIdleAsync();

        Assert.Equal(2, _hits[_Path(7)]); // the 404 and exactly one successful GET
        Assert.Equal(1, _hits[_Path(8)]); // the retry reads 8 from the cache
        Assert.Equal([7, 8], (await repository.GetForCharacterAsync(Mate, Ct)).Select(killmail => killmail.KillmailId).Order());
        foreach (int mate in new[] { SecondMate, ThirdMate })
        {
            LocalKillmail kill = Assert.Single(await repository.GetForCharacterAsync(mate, Ct));
            Assert.Equal(7, kill.KillmailId);
            Assert.False(kill.IsLoss);
            Assert.Equal(FleetId, kill.SharedFromFleetId);
        }
    }

    /// <summary>Only the intended remote fleet row may disappear; own rows and another fleet's history are the
    /// counterproof. Every row starts from the same state: the mate shared mails 1 and 2 in fleet 42 (UnixMs 10) and
    /// mail 3 in fleet 43, and the own character holds mail 1 itself.</summary>
    [Theory]
    [InlineData("newer-without-2", Mate, FleetId, 11, new[] { 1 }, new[] { 1, 3 }, new[] { 1 })]
    [InlineData("older-without-2", Mate, FleetId, 9, new[] { 1 }, new[] { 1, 2, 3 }, new[] { 1 })]
    [InlineData("other-fleet-empty", Mate, 43L, 11, new int[0], new[] { 1, 2 }, new[] { 1 })]
    [InlineData("own-echo", Own, FleetId, 11, new[] { 2 }, new[] { 1, 2, 3 }, new[] { 1 })]
    public async Task NewerShare_RemovesOnlyThatFleetsWithdrawnRemoteRow(string scenario, int sender, long fleetId,
        long unixMs, int[] killmailIds, int[] expectedMateIds, int[] expectedOwnIds)
    {
        Assert.NotEmpty(scenario);
        using TestClientInstance instance = _Instance();
        ILocalKillmailRepository repository = instance.Services.GetRequiredService<ILocalKillmailRepository>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Own Pilot", Own), Ct);
        await repository.AddMissingAsync(Own, [_OwnKill(1)], Ct);
        foreach (int id in new[] { 1, 2, 3 })
        {
            _routes[_Path(id)] = _ => Json(200, _Killmail(id, victim: Enemy, attackers: [Mate, Own]));
        }

        (FleetKillmailShareReceiver receiver, _) = _Receiver(instance);
        using IDisposable subscription = receiver;
        await _ShareAsync(instance, Mate, FleetId, 10, 1, 2);
        await _ShareAsync(instance, Mate, 43, 10, 3);
        await receiver.WhenIdleAsync();

        await _ShareAsync(instance, sender, fleetId, unixMs, killmailIds);
        await receiver.WhenIdleAsync();

        Assert.Equal(expectedMateIds, (await repository.GetForCharacterAsync(Mate, Ct)).Select(killmail => killmail.KillmailId).Order());
        Assert.Equal(expectedOwnIds, (await repository.GetForCharacterAsync(Own, Ct)).Select(killmail => killmail.KillmailId).Order());
    }

    private TestClientInstance _Instance() =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService());
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(Gila, "Gila", 26, 6)
                .AddSolarSystem(new SdeSolarSystem(Jita, "Jita", 0.946, "The Forge")));
        });

    private (FleetKillmailShareReceiver Receiver, StubHttpMessageHandler Stub) _Receiver(TestClientInstance instance)
    {
        var stub = new StubHttpMessageHandler((request, _) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            int call = _hits[path] = _hits.GetValueOrDefault(path) + 1;
            return _routes[path](call);
        });
        var cache = new EsiCacheHandler(new FileEsiCacheStore(_cacheDirectory),
            new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance)) { InnerHandler = stub };
        var client = new EsiClient(new SingleClientHttpFactory(new HttpClient(cache)),
            new FakeEsiTokenProvider(EsiAuthorization.Authorized("token"), [KillmailsScopeCatalog.ReadKillmails]),
            new EsiOutageDetector(new EsiAvailabilityState()), NullLogger<EsiClient>.Instance);
        var importer = new EsiKillmailImporter(client, instance.Services.GetRequiredService<ILocalKillmailRepository>(),
            instance.Services.GetRequiredService<IServiceScopeFactory>());
        var receiver = new FleetKillmailShareReceiver(instance.Services.GetRequiredService<IEventBus>(), importer,
            instance.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<FleetKillmailShareReceiver>.Instance);
        return (receiver, stub);
    }

    // The way the server echo arrives: a FleetKillmailShareEvent on the local bus, stamped with the sender.
    private static Task _ShareAsync(TestClientInstance instance, int sender, long fleetId, long unixMs,
        params int[] killmailIds) =>
        instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetKillmailShareEvent(new FleetKillmailShare
        {
            FleetId = fleetId,
            UnixMs = unixMs,
            Killmails = [.. killmailIds.Select(killmailId => new FleetKillmailReference
            {
                KillmailId = killmailId,
                Hash = _Hash(killmailId),
                KillmailTimeUtc = StartedAtUtc,
            })],
        }, sender), EventTarget.Local, Ct);

    private static string _Killmail(int killmailId, int victim, int[] attackers) => $$"""
        {"killmail_id":{{killmailId}},"killmail_time":"{{StartedAtUtc.AddMinutes(10):yyyy-MM-ddTHH:mm:ssZ}}","solar_system_id":{{Jita}},
         "victim":{"character_id":{{victim}},"ship_type_id":{{Gila}},"damage_taken":1,"items":[]},
         "attackers":[{{string.Join(",", attackers.Select((attacker, index) =>
             $$"""{"character_id":{{attacker}},"damage_done":1,"final_blow":{{(index == 0 ? "true" : "false")}}}"""))}}]}
        """;

    private static string _Hash(int killmailId) => killmailId.ToString("x40");

    private static string _Path(int killmailId) => $"/killmails/{killmailId}/{_Hash(killmailId)}/";

    private static LocalKillmail _OwnKill(int killmailId, int characterId = Own) => new()
    {
        CharacterId = characterId,
        KillmailId = killmailId,
        Hash = _Hash(killmailId),
        KillmailTimeUtc = StartedAtUtc.AddDays(-1),
        SolarSystemId = Jita,
        VictimShipTypeId = Gila,
        VictimCharacterId = Enemy,
        ImportedAtUtc = DateTime.UtcNow,
    };

    private static IEnumerable<int> _VisibleIds(KillmailsOverviewViewModel viewModel) =>
        viewModel.Days.SelectMany(day => day.Rows).Select(row => row.KillmailId);

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (int i = 0; i < tries; i++)
        {
            UiDispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }
}
