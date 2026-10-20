using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-517: where the BY ACTIVITY list gets its figures. A server's shared figure when the pilot opted in and enough
/// pilots share, the own runs otherwise — and a server is asked which activities it holds figures for before any figure
/// is asked of it, so an activity nobody else flew never costs a call.
/// </summary>
public sealed class ActivityStatsServiceTests
{
    private const long Pilot = 90000001;
    private const string Server = "https://krah.invalid";
    private const string OtherServer = "https://alliance.invalid";
    private const int SiteTypeId = 2311;
    private const int Tritanium = 34;
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, SiteTypeId);
    private static readonly ActivityStatsKey SmallSite = ActivityStatsKey.Site(SiteTypeSource.Site, 2316);
    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The usual case: opted in, enough pilots. The row's headline is the server's figure, the own runs stay
    /// beside it as YOURS and for the dots, and nothing is said about a fallback. Counter-proof: read the own runs only
    /// and the typical is 3,000, not 35M.</summary>
    [AvaloniaFact]
    public async Task ASharedFigure_IsTheRowsBasis_AndTheOwnRunsStayBesideIt()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.List(Server, Sanctum, runs: 6, pilots: 3);

        ActivityStats stats = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(EarningsBasis.AllPilots, stats.Basis?.Basis);
        Assert.Equal(35_000_000m, stats.Basis?.Figures.Typical);
        Assert.Equal(OwnRunsReason.None, stats.Reason);
        Assert.Equal(EarningsBasis.YourRuns, stats.Own?.Basis);
        Assert.Equal(5, stats.Yours?.RunCount);
        Assert.Equal(3_000m, stats.Yours?.Typical);
        Assert.Equal("Angel Sanctum", stats.SiteName);
    }

    /// <summary>An activity the server does not list has fewer than two pilots behind it: it is never asked for, and the
    /// row says so. Counter-proof: ask for every key and the loot call count goes up.</summary>
    [AvaloniaFact]
    public async Task AnActivityNobodyElseFlew_IsNeverAskedOfTheServer_AndFallsBackToTheOwnRuns()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        ActivityStats stats = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(EarningsBasis.YourRuns, stats.Basis?.Basis);
        Assert.Equal(OwnRunsReason.TooFewPilots, stats.Reason);
        Assert.Empty(fixture.Outcomes.OutcomeCalls);
        Assert.Equal(1, fixture.Outcomes.ListCalls);
    }

    /// <summary>Reading a server is part of opting in: without it the server is not even asked what it holds.
    /// Counter-proof: drop the opt-in check and the list is read.</summary>
    [AvaloniaFact]
    public async Task NotOptedIn_NeverAsksTheServer_AndSaysSo()
    {
        using Fixture fixture = await Fixture.CreateAsync(optedIn: false);
        fixture.Outcomes.List(Server, Sanctum, runs: 6, pilots: 3);

        ActivityStats stats = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(EarningsBasis.YourRuns, stats.Basis?.Basis);
        Assert.Equal(OwnRunsReason.NotSharing, stats.Reason);
        Assert.Equal(0, fixture.Outcomes.ListCalls);
        Assert.Empty(fixture.Outcomes.OutcomeCalls);
    }

    [AvaloniaFact]
    public async Task AServerThatCannotAnswer_IsAReasonOnTheRow_NotAnError()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Unreachable = true;

        ActivityStats stats = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(EarningsBasis.YourRuns, stats.Basis?.Basis);
        Assert.Equal(OwnRunsReason.ServerUnavailable, stats.Reason);
    }

    /// <summary>A figure that was read stays for ten minutes: a second read — every change to a run makes one — costs no
    /// call. Counter-proof: no cache and the second read asks both again.</summary>
    [AvaloniaFact]
    public async Task ASecondReadInsideTenMinutes_IsAnsweredFromTheCache()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.List(Server, Sanctum, runs: 6, pilots: 3);

        await fixture.ReadLocalAsync();
        ActivityStats again = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(1, fixture.Outcomes.ListCalls);
        Assert.Single(fixture.Outcomes.OutcomeCalls);
        Assert.Equal(EarningsBasis.AllPilots, again.Basis?.Basis);
    }

    /// <summary>The Server tab shows ALL PILOTS of that server only: its own list, its own figures, no YOURS. An activity
    /// with too few runs has no figure, only how many it has. Counter-proof: ask the other server's list too and its
    /// activity shows up on this tab.</summary>
    [AvaloniaFact]
    public async Task ATabsServer_ListsOnlyItsOwnActivities_WithoutYours()
    {
        using Fixture fixture = await Fixture.CreateAsync(secondServer: true);
        fixture.Outcomes.List(Server, Sanctum, runs: 6, pilots: 3);
        fixture.Outcomes.List(Server, SmallSite, runs: 3, pilots: 2);
        fixture.Outcomes.List(OtherServer, ActivityStatsKey.Site(SiteTypeSource.Site, 9999), runs: 8, pilots: 4);

        ActivityStatsOverview overview = await fixture.ReadAsync(Server);

        Assert.Equal([Sanctum, SmallSite], overview.Activities.Select(stats => stats.Key).OrderBy(key => key.TypeId));
        ActivityStats fine = overview.Activities.Single(stats => stats.Key == Sanctum);
        ActivityStats thin = overview.Activities.Single(stats => stats.Key == SmallSite);
        Assert.Null(fine.Yours);
        Assert.Equal(Server, fine.Basis?.ServerAddress);
        Assert.Null(thin.Basis);
        Assert.Equal(3, thin.EffectiveRuns);
        Assert.Null(overview.Status);
    }

    [AvaloniaFact]
    public async Task ATabForAServerNotSharedWith_SaysHowToTurnItOn()
    {
        using Fixture fixture = await Fixture.CreateAsync(optedIn: false);

        ActivityStatsOverview overview = await fixture.ReadAsync(Server);

        Assert.Empty(overview.Activities);
        Assert.Contains("Turn on sharing in Settings", overview.Status);
        Assert.Equal(0, fixture.Outcomes.ListCalls);
    }

    /// <summary>Several servers: every answer is kept for the chip's list, and the server the pilot picked by hand feeds the
    /// figure from then on — also once the first read is cached. Counter-proof: ignore the pick and the one with the most
    /// data wins.</summary>
    [AvaloniaFact]
    public async Task APickedServer_FeedsTheFigure_AndEveryAnswerStaysForTheList()
    {
        using Fixture fixture = await Fixture.CreateAsync(secondServer: true);
        fixture.Outcomes.List(Server, Sanctum, runs: 9, pilots: 4);
        fixture.Outcomes.List(OtherServer, Sanctum, runs: 6, pilots: 2);

        ActivityStats before = Assert.Single((await fixture.ReadLocalAsync()).Activities);
        await fixture.Instance.Services.GetRequiredService<ISharedServerPicks>().SetAsync(OtherServer, Token);
        ActivityStats after = Assert.Single((await fixture.ReadLocalAsync()).Activities);

        Assert.Equal(Server, before.Basis?.ServerAddress);
        Assert.Equal(OtherServer, after.Basis?.ServerAddress);
        Assert.Equal([OtherServer, Server], after.Answers.Select(answer => answer.ServerAddress).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePick_BeatsTheRunsServer_AndAMissingServerIsIgnored()
    {
        ExpectedEarnings small = _Answer(Server, runs: 6);
        ExpectedEarnings big = _Answer(OtherServer, runs: 40);

        Assert.Equal(Server, SharedServerChoice.Pick([big, small], runServer: OtherServer, pinnedServer: Server).ServerAddress);
        Assert.Equal(OtherServer, SharedServerChoice.Pick([big, small], runServer: null, pinnedServer: "https://gone.invalid").ServerAddress);
    }

    [AvaloniaFact]
    public async Task ThePreferredServer_IsRememberedAcrossReads()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        ISharedServerPicks picks = fixture.Instance.Services.GetRequiredService<ISharedServerPicks>();

        Assert.Null(await picks.LoadAsync(Token));
        await picks.SetAsync(Server, Token);

        Assert.Equal(Server, await picks.LoadAsync(Token));
    }

    private static ExpectedEarnings _Answer(string server, int runs)
    {
        EarningsSample[] samples = [.. Enumerable.Range(0, runs).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index}", CompletedAtUtc = DateTime.UtcNow.AddDays(-1), FixedIsk = 1_000_000m,
            LootBasis = LootBasis.CargoDifference
        })];
        return new ExpectedEarnings(EarningsBasis.AllPilots,
            EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), DateTime.UtcNow), 2, server, server);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(TestClientInstance instance, ScriptedServer outcomes)
        {
            Instance = instance;
            Outcomes = outcomes;
        }

        public TestClientInstance Instance { get; }
        public ScriptedServer Outcomes { get; }

        public static async Task<Fixture> CreateAsync(bool optedIn = true, bool secondServer = false)
        {
            var outcomes = new ScriptedServer();
            var connector = new FakeRemoteBusConnector();
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteBusConnector>(connector);
                services.AddSingleton<IServerRunOutcomeClient>(outcomes);
            });
            var fixture = new Fixture(instance, outcomes);
            IClientSessionStore sessions = instance.Services.GetRequiredService<IClientSessionStore>();
            RunOutcomeSharingChoices choices = instance.Services.GetRequiredService<RunOutcomeSharingChoices>();
            await sessions.SaveAsync(Server, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
            connector.RaiseCharacterStateChanged(Server, (int)Pilot, ServerConnectionState.Connected);
            if (optedIn)
                await choices.SetAsync(Server, OutcomeSharingChoice.Shared, Token);
            if (secondServer)
            {
                await sessions.SaveAsync(OtherServer, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
                connector.RaiseCharacterStateChanged(OtherServer, (int)Pilot, ServerConnectionState.Connected);
                await choices.SetAsync(OtherServer, OutcomeSharingChoice.Shared, Token);
            }

            await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
                [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }], Token);
            IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
            for (int index = 0; index < 5; index++)
                await _SaveAsync(dispatcher, Today.AddDays(-index).AddHours(10), 10 * (index + 1));
            return fixture;
        }

        public Task<ActivityStatsOverview> ReadLocalAsync() => ReadAsync(null);

        public Task<ActivityStatsOverview> ReadAsync(string? server) =>
            Instance.Services.GetRequiredService<IActivityStatsSource>().ReadAsync(
                new ActivityStatsRequest(Today.AddDays(-7), Today.AddDays(1), server, [(int)Pilot]), Token);

        public void Dispose() => Instance.Dispose();

        private static async Task _SaveAsync(IDispatcher dispatcher, DateTime startedAtUtc, int tritanium)
        {
            Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, SiteTypeId,
                "Angel Sanctum", 30000142), Token);
            await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
                [
                    new RunLootCaptureInput
                    {
                        CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                        ContentHash = $"{startedAtUtc:O}",
                        Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = tritanium, LootKind = LootKind.Gained }]
                    }
                ], [], [], []), Token);
        }
    }

    private sealed class ScriptedServer : IServerRunOutcomeClient
    {
        private readonly Dictionary<string, List<RunOutcomeKeyCountDto>> _keys = [];
        private readonly Dictionary<(string Server, ActivityStatsKey Key), (int Runs, int Pilots)> _figures = [];

        public bool Unreachable { get; set; }
        public int ListCalls { get; private set; }
        public List<(string Server, ActivityStatsKey Key)> OutcomeCalls { get; } = [];

        public void List(string server, ActivityStatsKey key, int runs, int pilots)
        {
            if (!_keys.TryGetValue(server, out List<RunOutcomeKeyCountDto>? keys))
                _keys[server] = keys = [];
            keys.Add(new RunOutcomeKeyCountDto { Key = key, RunCount = runs, EffectiveRunCount = runs, PilotCount = pilots });
            _figures[(server, key)] = (runs, pilots);
        }

        public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
            RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>> ListOutcomeKeysAsync(string serverAddress, long characterId,
            DateOnly since, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(Unreachable
                ? OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>.Transient("down")
                : new OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>(_keys.GetValueOrDefault(serverAddress) ?? [], "", ""));
        }

        public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            OutcomeCalls.Add((serverAddress, key));
            (int runs, int pilots) = _figures.GetValueOrDefault((serverAddress, key));
            return Task.FromResult(new OutcomeReadResult<ActivityOutcomesDto>(_Outcomes(key, runs, pilots), "", ""));
        }

        public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            (int runs, int pilots) = _figures.GetValueOrDefault((serverAddress, key));
            return Task.FromResult(new OutcomeReadResult<ActivityOutcomeLootDto>(new ActivityOutcomeLootDto
            {
                Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
                Outcomes = [.. Enumerable.Range(1, runs).Select(index => new RunOutcomeLootDto
                {
                    CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)), FleetSize = 1,
                    LootBasis = LootBasis.CargoDifference, Weight = 1, Lines = []
                })]
            }, "", ""));
        }

        private static ActivityOutcomesDto _Outcomes(ActivityStatsKey key, int runs, int pilots) => new()
        {
            Key = key, Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
            Outcomes = [.. Enumerable.Range(1, runs).Select(index => new RunOutcomeDto
            {
                CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)), DurationSeconds = 600 + index * 60,
                FixedIsk = index * 10_000_000m, FleetSize = 1, LootBasis = LootBasis.CargoDifference,
                OpenUnrecognisedLootCount = 0, Weight = 1
            })]
        };
    }
}
