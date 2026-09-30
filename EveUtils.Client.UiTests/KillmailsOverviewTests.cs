using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-332: the KILLMAILS overview — the SHOW filter narrows to what it claims, efficiency guards against an empty
/// or unpriced character, a character without the scope shows GRANT ACCESS instead of an empty list, a search hits
/// ship, pilot and system alike, mails group under their local calendar day newest first, and an unpriced mail reads
/// "no price" rather than 0 ISK. One test per acceptance criterion.
/// </summary>
public sealed class KillmailsOverviewTests
{
    private const int Pilot = 90000001;
    private const int OtherPilot = 90000050;
    private const int System1 = 30000142; // Jita
    private const int Gila = 20125;
    private const int Vexor = 626;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Criterion 1. Red if Not linked also shows the linked loss or the kill.</summary>
    [Theory]
    [InlineData(KillmailShowFilter.Losses, new[] { 2, 3 })]
    [InlineData(KillmailShowFilter.NotLinked, new[] { 3 })]
    public async Task ShowFilter_NarrowsToWhatItClaims(KillmailShowFilter filter, int[] expectedKillmailIds)
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher);
        await _AddAsync(instance, _Kill(1), _Loss(2, linkedRunId: null), _Loss(3, linkedRunId: null));
        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 2, runId), Ct);
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        viewModel.Filters.Single(tile => tile.Key == filter).SelectCommand.Execute(null);

        Assert.Equal(expectedKillmailIds.OrderBy(id => id), _VisibleIds(viewModel).OrderBy(id => id));
    }

    /// <summary>Criterion 2. Red if an empty or fully unpriced character shows 0% or throws instead of "—".</summary>
    [Theory]
    [InlineData(false, "—")]
    [InlineData(true, "66.7%")]
    public async Task Efficiency_IsDestroyedOverDestroyedPlusLost_OrADashWhenThereIsNothingToDivide(bool seedPricedMails, string expected)
    {
        using TestClientInstance instance = TestClientInstance.Create();
        if (seedPricedMails)
        {
            IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
            Guid runId = await _SaveRunAsync(dispatcher);
            await _PriceAsync(instance, (Gila, 2_000_000), (Vexor, 1_000_000));
            await _AddAsync(instance, _Kill(1, Gila), _Loss(2, linkedRunId: null, Vexor));
            await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 2, runId), Ct);
        }

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        Assert.Equal(expected, viewModel.EfficiencyText);
    }

    /// <summary>Criterion 3. Red if a character without the scope shows an empty list instead of GRANT ACCESS. The
    /// contrast — a character that did grant it reads normally — is every other criterion here: all of them load
    /// with <c>hasScope: true</c> and see their seeded mails.</summary>
    [AvaloniaFact]
    public async Task Character_WithoutTheScope_ShowsGrantAccess_NotAnEmptyList()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _AddAsync(instance, _Kill(1)); // present in storage, so an empty Days here can only mean the gate, not a genuinely empty character

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: false);
        await _SelectAsync(viewModel, Pilot);

        Assert.True(viewModel.NeedsAccess);
        Assert.Empty(viewModel.Days);
        Assert.True(viewModel.SelectedCharacter?.NeedsAccess);
    }

    /// <summary>ET-363 AC1. Red if the tile only flips after a reopen: GRANT ACCESS raises
    /// <see cref="ICharacterRegistry.RegistryChanged"/> (the same signal <c>SkillRefreshService</c>,
    /// <c>ImplantRefreshService</c> and <c>MetricsWindowViewModel</c> already react to), and this screen has to catch
    /// it live rather than only through <see cref="KillmailsOverviewViewModel.RefreshModule"/> (ET-46's reopen path).</summary>
    [AvaloniaFact]
    public async Task Character_ScopeGrantedWhileOpen_FlipsTheTileWithoutReopening()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        ICharacterRegistry registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Test Pilot", Pilot, GrantedScopes: []), Ct);
        await _AddAsync(instance, _Kill(1));
        KillmailsOverviewViewModel viewModel = new(instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), instance.Services, [new Character("Test Pilot", Pilot, GrantedScopes: [])],
            (_, _) => Task.CompletedTask);
        await viewModel.LoadAsync(Ct);
        await _SelectAsync(viewModel, Pilot);
        Assert.True(viewModel.NeedsAccess);

        await registry.AddOrUpdateAsync(new Character("Test Pilot", Pilot, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails]), Ct);

        // NeedsAccess flips to false the moment the triggered read starts, not when it finishes (_ReadAsync's own
        // ordering) — wait for the read to settle too, or the assertion below could catch Days still empty.
        Assert.True(await _WaitForAsync(() => !viewModel.NeedsAccess && !viewModel.IsBusy));
        Assert.False(viewModel.SelectedCharacter?.NeedsAccess);
        Assert.Equal([1], _VisibleIds(viewModel));
        viewModel.Dispose();
    }

    /// <summary>ET-363 AC3. Red if a killmail the background refresh finds while KILLMAILS is open sits invisible
    /// until the pilot reopens it. The import stores through <see cref="StoreKillmailsCommand"/>, whose signal reaches the
    /// screen on the same feed a run link does (ET-383).</summary>
    [AvaloniaFact]
    public async Task NewKillmailFoundInTheBackground_AppearsWithoutReopening()
    {
        RoutingEsiClient esi = new();
        esi.Responses[$"/characters/{Pilot}/killmails/recent/?page=1"] = new[] { new EsiKillmailRef { KillmailId = 2, KillmailHash = "hash2" } };
        esi.Responses["/killmails/2/hash2/"] = new EsiKillmail
        {
            KillmailId = 2, KillmailTime = DateTimeOffset.UtcNow, SolarSystemId = System1,
            Victim = new EsiKillmailVictim { CharacterId = Pilot + 1000, ShipTypeId = Vexor, DamageTaken = 1 },
            Attackers = [new EsiKillmailAttacker { CharacterId = Pilot, DamageDone = 1, FinalBlow = true }]
        };
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<IEsiClient>(esi));
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Test Pilot", Pilot, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails]), Ct);
        await _AddAsync(instance, _Kill(1));
        KillmailsOverviewViewModel viewModel = new(instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), instance.Services,
            [new Character("Test Pilot", Pilot, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails])], (_, _) => Task.CompletedTask);
        await viewModel.LoadAsync(Ct);
        Assert.Equal([1], _VisibleIds(viewModel));

        // Stands in for the background KillmailRefreshService's own 5-minute tick finding this mail — no scope or
        // character change involved, just a new killmail landing while the window is already open.
        await instance.Services.GetRequiredService<EsiKillmailImporter>().ImportAsync(Pilot, Ct);

        Assert.True(await _WaitForAsync(() => _VisibleIds(viewModel).Contains(2)));
        Assert.Equal([1, 2], _VisibleIds(viewModel).OrderBy(id => id));
        viewModel.Dispose();
    }

    /// <summary>ET-382. Red if a link made by another window (the run window's own LINK LOSS) leaves this screen
    /// listing the loss as not linked until the pilot reopens it.</summary>
    [AvaloniaFact]
    public async Task LossLinkedFromAnotherWindow_LeavesTheNotLinkedList_WithoutReopening()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher);
        await _AddAsync(instance, _Loss(2, linkedRunId: null));
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);
        viewModel.Filters.Single(tile => tile.Key == KillmailShowFilter.NotLinked).SelectCommand.Execute(null);
        Assert.Equal([2], _VisibleIds(viewModel));

        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 2, runId), Ct);

        Assert.True(await _WaitForAsync(() => !_VisibleIds(viewModel).Any()));
        viewModel.Dispose();
    }

    /// <summary>ET-405 AC3. Red if a killmail two own characters share is listed once per character, or counted twice in
    /// the totals: All shows the distinct mails, the sum of the per-character screens does not. Filtering on one
    /// character still gives that character's own list, as the per-character screen did.</summary>
    [AvaloniaFact]
    public async Task AllCharacters_ListsASharedKillmailOnce_AndCountsItOnce()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _PriceAsync(instance, (Gila, 2_000_000), (Vexor, 1_000_000));
        await _AddAsync(instance, Pilot, _Kill(1), _Kill(2), _Loss(3, linkedRunId: null));
        await _AddAsync(instance, OtherPilot, _Kill(2, characterId: OtherPilot), _Kill(4, characterId: OtherPilot));
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, _Granted(Pilot, "Alpha"), _Granted(OtherPilot, "Bravo"));

        Assert.True(viewModel.SelectedCharacter?.IsAll);
        Assert.Equal([1, 2, 3, 4], _VisibleIds(viewModel).OrderBy(id => id));
        Assert.Equal(3, viewModel.KillsCount);
        Assert.Equal(1, viewModel.LossesCount);
        Assert.Equal("6M", viewModel.IskDestroyedText);
        Assert.Equal("1M", viewModel.IskLostText);
        KillmailRowViewModel shared = viewModel.Days.SelectMany(day => day.Rows).Single(row => row.KillmailId == 2);
        Assert.Equal(["Alpha", "Bravo"], shared.Pilots.Select(pilot => pilot.Name));
        Assert.Equal(1, shared.ExtraPilotCount);

        await _SelectAsync(viewModel, Pilot);
        Assert.Equal([1, 2, 3], _VisibleIds(viewModel).OrderBy(id => id));
        int killsOfPilot = viewModel.KillsCount;
        await _SelectAsync(viewModel, OtherPilot);
        Assert.Equal([2, 4], _VisibleIds(viewModel).OrderBy(id => id));
        Assert.Equal(4, killsOfPilot + viewModel.KillsCount); // the per-character sum counts the shared mail twice
        viewModel.Dispose();
    }

    /// <summary>ET-405. Red if a mail that is a loss for one own character and a kill for another shows as a kill, or
    /// as two rows: the lost ship is the story of the mail and it counts once, as a loss.</summary>
    [AvaloniaFact]
    public async Task AllCharacters_AMailThatIsALossForOneAndAKillForAnother_IsOneLoss()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _AddAsync(instance, Pilot, _Loss(7, linkedRunId: null));
        await _AddAsync(instance, OtherPilot, _Kill(7, characterId: OtherPilot));
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, _Granted(Pilot, "Alpha"), _Granted(OtherPilot, "Bravo"));

        KillmailRowViewModel row = viewModel.Days.SelectMany(day => day.Rows).Single();

        Assert.True(row.IsLoss);
        Assert.Equal(1, viewModel.LossesCount);
        Assert.Equal(0, viewModel.KillsCount);
        Assert.Equal(2, row.Pilots.Count);
        viewModel.Dispose();
    }

    /// <summary>ET-405 AC1. Red if the filter is not one control that fits: six characters, a 700 px window, and the
    /// dropdown stays inside it with "All characters" first — the chip row it replaced ran off the right edge.</summary>
    [AvaloniaFact]
    public async Task CharacterFilter_IsOneDropdown_ThatFitsA700pxWindow_WithAllFirst()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Character[] characters = [.. Enumerable.Range(0, 6).Select(index => _Granted(Pilot + index, $"Pilot number {index} Longname"))];
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, characters);
        KillmailsWindow window = new(viewModel) { Width = 700, Height = 600 };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        ComboBox filter = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "CharacterFilter");
        Point topLeft = filter.TranslatePoint(default, window) ?? default;
        Assert.InRange(filter.Bounds.Width, 1, 700);
        Assert.True(topLeft.X + filter.Bounds.Width <= window.Bounds.Width, $"right edge {topLeft.X + filter.Bounds.Width}");
        Assert.Equal(7, viewModel.CharacterOptions.Count);
        Assert.True(viewModel.CharacterOptions[0].IsAll);
        Assert.Same(viewModel.CharacterOptions[0], filter.SelectedItem);
        window.Close();
        viewModel.Dispose();
    }

    /// <summary>ET-405 AC5. Red if a character without the scope vanishes from the dropdown or its entry cannot start
    /// the grant: it is listed, flagged, and its own command reaches the allow-scope callback with its id.</summary>
    [AvaloniaFact]
    public async Task CharacterFilter_ListsACharacterWithoutAccess_WhoseGrantAccessStaysReachable()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        List<int> granted = [];
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance,
            [_Granted(Pilot, "Alpha"), new Character("Bravo", OtherPilot, GrantedScopes: [])],
            (id, _) => { granted.Add(id); return Task.CompletedTask; });

        KillmailCharacterOptionViewModel bravo = viewModel.CharacterOptions.Single(option => option.CharacterId == OtherPilot);
        bravo.GrantAccessCommand.Execute(null);

        Assert.True(bravo.NeedsAccess);
        Assert.False(viewModel.CharacterOptions.Single(option => option.CharacterId == Pilot).NeedsAccess);
        Assert.Equal([OtherPilot], granted);
        viewModel.Dispose();
    }

    private sealed class RoutingEsiClient : IEsiClient
    {
        public Dictionary<string, object?> Responses { get; } = new();

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Responses.TryGetValue(request.Path, out object? value) && value is T typed
                ? EsiResult<T>.Ok(typed)
                : EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, $"no stub for {request.Path}", 500)));
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (int i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>Criterion 4. Red if only the ship name is searched.</summary>
    [Theory]
    [InlineData("tama", true)]      // the system
    [InlineData("gila", true)]      // the ship
    [InlineData("holloway", true)]  // the pilot
    [InlineData("uedama", false)]   // none of the three
    public void Matches_SearchesShipSystemAndPilot(string needle, bool expected)
    {
        KillmailRowViewModel row = _Row(shipName: "Gila", systemName: "Tama", counterparty: "Dexter Holloway");

        Assert.Equal(expected, row.Matches(needle));
    }

    /// <summary>Criterion 5. Red if a mail near UTC midnight lands under the wrong day — the exact rood-condition the
    /// ticket names. A fixed UTC+2 clock (never this machine's own zone, so the test is the same on every machine)
    /// makes it deterministic: two mails on the same UTC calendar day land on two different LOCAL days once the
    /// offset carries one of them past midnight.</summary>
    [Fact]
    public async Task Days_GroupByLocalCalendarDay_NotUtc()
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("ET332-UTC+2", TimeSpan.FromHours(2), "ET332-UTC+2", "ET332-UTC+2");
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<TimeProvider>(new FixedZoneTimeProvider(zone)));
        DateTime nearUtcMidnight = new(2026, 3, 15, 23, 30, 0, DateTimeKind.Utc); // local 01:30 on the 16th (+2)
        DateTime sameUtcDayEarlier = new(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc); // local noon on the 15th — same UTC day as above, different local day
        DateTime earlierUtcDay = new(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc);
        await _AddAsync(instance, _Kill(1, atUtc: nearUtcMidnight), _Kill(2, atUtc: sameUtcDayEarlier), _Kill(3, atUtc: earlierUtcDay));

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        // Grouping by raw UTC date would merge mail 1 and mail 2 (both UTC 15 March) into one day; grouping by local
        // date (the requirement) puts mail 1 alone under the 16th, ahead of mail 2's day, newest first.
        Assert.Equal(3, viewModel.Days.Count);
        Assert.Equal(new DateOnly(2026, 3, 16), viewModel.Days[0].Day);
        Assert.Equal(1, viewModel.Days[0].Rows.Single().KillmailId);
        Assert.Equal(new DateOnly(2026, 3, 15), viewModel.Days[1].Day);
        Assert.Equal(2, viewModel.Days[1].Rows.Single().KillmailId);
        Assert.Equal(new DateOnly(2026, 3, 12), viewModel.Days[2].Day);
        Assert.Equal(3, viewModel.Days[2].Rows.Single().KillmailId);
    }

    /// <summary>Criterion 6. Red if an unpriced mail shows 0 ISK instead of "no price".</summary>
    [Theory]
    [InlineData(null, "no price")]
    [InlineData(1_000_000L, "1M")]
    public void IskText_IsNoPrice_NeverAZeroFigure_WhenNothingIsPriced(long? iskValue, string expected)
    {
        KillmailRowViewModel row = _Row(iskValue: iskValue is { } value ? value : null);

        Assert.Equal(expected, row.IskText);
    }

    private static Task<KillmailsOverviewViewModel> _LoadAsync(TestClientInstance instance, bool hasScope) =>
        _LoadAsync(instance, new Character("Test Pilot", Pilot, GrantedScopes: hasScope ? [KillmailsScopeCatalog.ReadKillmails] : []));

    private static Task<KillmailsOverviewViewModel> _LoadAsync(TestClientInstance instance, params Character[] characters) =>
        _LoadAsync(instance, characters, (_, _) => Task.CompletedTask);

    private static async Task<KillmailsOverviewViewModel> _LoadAsync(TestClientInstance instance, Character[] characters,
        Func<int, string, Task> allowScope)
    {
        KillmailsOverviewViewModel viewModel = new(instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), instance.Services, characters, allowScope);
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    private static Character _Granted(int characterId, string name) =>
        new(name, characterId, GrantedScopes: [KillmailsScopeCatalog.ReadKillmails]);

    private static async Task _SelectAsync(KillmailsOverviewViewModel viewModel, int characterId)
    {
        viewModel.SelectedCharacter = viewModel.CharacterOptions.Single(option => option.CharacterId == characterId);
        Assert.True(await _WaitForAsync(() => !viewModel.IsBusy));
    }

    private static IEnumerable<int> _VisibleIds(KillmailsOverviewViewModel viewModel) =>
        viewModel.Days.SelectMany(day => day.Rows).Select(row => row.KillmailId);

    private static LocalKillmail _Kill(int killmailId, int shipTypeId = Gila, DateTime? atUtc = null, int characterId = Pilot) => new()
    {
        CharacterId = characterId,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = atUtc ?? DateTime.UtcNow,
        SolarSystemId = System1,
        IsLoss = false,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = Pilot + 1000,
        LinkSource = KillmailLinkSource.None,
        ImportedAtUtc = DateTime.UtcNow,
        Attackers = [new LocalKillmailAttacker { CharacterId = characterId, KillmailId = killmailId, Ordinal = 0, AttackerCharacterId = characterId, FinalBlow = true, DamageDone = 100 }]
    };

    private static LocalKillmail _Loss(int killmailId, Guid? linkedRunId, int shipTypeId = Vexor) => new()
    {
        CharacterId = Pilot,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = DateTime.UtcNow,
        SolarSystemId = System1,
        IsLoss = true,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = Pilot,
        RunId = linkedRunId,
        LinkSource = linkedRunId is null ? KillmailLinkSource.None : KillmailLinkSource.Auto,
        ImportedAtUtc = DateTime.UtcNow
    };

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher)
    {
        DateTime startedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 0, null, System1), Ct);
        Result saved = await dispatcher.Send(
            new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20), startedAtUtc.AddMinutes(21), [], [], [], []), Ct);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }

    private static Task _AddAsync(TestClientInstance instance, params LocalKillmail[] killmails) =>
        _AddAsync(instance, Pilot, killmails);

    private static Task _AddAsync(TestClientInstance instance, int characterId, params LocalKillmail[] killmails) =>
        instance.Services.GetRequiredService<ILocalKillmailRepository>().AddMissingAsync(characterId, killmails, Ct);

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            .. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })
        ], Ct);

    private static KillmailRowViewModel _Row(string shipName = "Gila", string systemName = "Tama", string counterparty = "NPC",
        long? iskValue = null)
    {
        KillmailOverviewRowDto dto = new(Pilot, 1, DateTime.UtcNow, System1, IsLoss: false, VictimShipTypeId: Gila,
            VictimCharacterId: null, VictimCorporationId: null, VictimAllianceId: null, AttackerCount: 1, FinalBlow: null,
            RunId: null, LinkSource: KillmailLinkSource.None, NotLinkedCandidateCount: 0,
            IskValue: iskValue is { } value ? value : null);
        return new KillmailRowViewModel(dto, [new CharacterFaceViewModel(Pilot, "Test Pilot")], shipName, systemName, regionName: null, isAbyssal: false, securityText: "0.5",
            counterpartyName: counterparty, TimeZoneInfo.Utc, openDetail: _ => Task.CompletedTask);
    }

    private sealed class FixedZoneTimeProvider(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = zone;
    }
}
