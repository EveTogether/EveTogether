using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-516: where the panel's figure comes from. A server's shared figure when the pilot opted in and enough pilots
/// share; the own runs otherwise, with the reason. The server is a double that answers per window, so the 90-day
/// figure, the 365-day fallback and every refusal can be told apart.
/// </summary>
public sealed class ExpectedEarningsServiceTests
{
    private const int Pilot = 90000001;
    private const string Server = "https://krah.invalid";
    private const string OtherServer = "https://alliance.invalid";
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task NoServerCoupled_HasNothingToSay()
    {
        using Fixture fixture = await Fixture.CreateAsync(couple: false);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.None, attempt.Reason);
    }

    /// <summary>Reading is part of the exchange: a pilot who shares nothing is not handed the others' figures. Counter-proof:
    /// drop the opt-in check and the server is asked.</summary>
    [AvaloniaFact]
    public async Task NotOptedIn_FallsBackToOwnRuns_AndNeverAsksTheServer()
    {
        using Fixture fixture = await Fixture.CreateAsync(optedIn: false);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Equal(OwnRunsReason.NotSharing, attempt.Reason);
        Assert.Empty(fixture.Outcomes.OutcomeCalls);
    }

    [AvaloniaFact]
    public async Task OptedIn_WithEnoughPilots_ReadsTheSharedFigure()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ => Scripted.Shared(Sanctum, count: 6, pilots: 3);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        ExpectedEarnings shared = Assert.IsType<ExpectedEarnings>(attempt.Shared);
        Assert.Equal(EarningsBasis.AllPilots, shared.Basis);
        Assert.Equal(3, shared.PilotCount);
        Assert.Equal(35_000_000m, shared.Figures.Typical);
        Assert.Equal(EarningsWindow.Recent, shared.Figures.Window);
        Assert.Equal(10_000_000m, shared.Figures.Worst!.YieldIsk);
        Assert.Equal(60_000_000m, shared.Figures.Best!.YieldIsk);
        Assert.Single(fixture.Outcomes.OutcomeCalls);
    }

    /// <summary>Several coupled servers (decision 10): without a server of the run, the one with the most data for this
    /// activity feeds the figure. Counter-proof: take the first by address and the smaller server wins.</summary>
    [AvaloniaFact]
    public async Task SeveralServers_TheOneWithTheMostDataFeedsTheFigure()
    {
        using Fixture fixture = await Fixture.CreateAsync(secondServer: true);
        fixture.Outcomes.PerServer[OtherServer] = _ => Scripted.Shared(Sanctum, count: 6, pilots: 2);
        fixture.Outcomes.PerServer[Server] = _ => Scripted.Shared(Sanctum, count: 9, pilots: 4);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Equal(Server, Assert.IsType<ExpectedEarnings>(attempt.Shared).ServerAddress);
        Assert.Equal(4, attempt.Shared.PilotCount);
    }

    [AvaloniaFact]
    public async Task SeveralServers_OneThatCannotAnswer_DoesNotStopTheOther()
    {
        using Fixture fixture = await Fixture.CreateAsync(secondServer: true);
        fixture.Outcomes.PerServer[Server] = _ => Scripted.TooFewPilots(Sanctum, pilots: 1);
        fixture.Outcomes.PerServer[OtherServer] = _ => Scripted.Shared(Sanctum, count: 6, pilots: 3);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Equal(OtherServer, attempt.Shared!.ServerAddress);
    }

    /// <summary>The server the run belongs to wins over the one with more data, whatever the sizes.</summary>
    [Fact]
    public void TheServerOfTheRun_WinsOverTheOneWithMoreData()
    {
        ExpectedEarnings small = Shared(Server, runs: 6);
        ExpectedEarnings big = Shared(OtherServer, runs: 40);

        Assert.Equal(Server, SharedServerChoice.Pick([big, small], Server).ServerAddress);
        Assert.Equal(OtherServer, SharedServerChoice.Pick([small, big], runServer: null).ServerAddress);
        Assert.Equal(OtherServer, SharedServerChoice.Pick([small, big], "https://elsewhere.invalid").ServerAddress);
    }

    [AvaloniaFact]
    public async Task TooFewPilots_FallsBackToOwnRuns_WithoutReadingAnyLoot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ => Scripted.TooFewPilots(Sanctum, pilots: 1);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.TooFewPilots, attempt.Reason);
        Assert.Equal(0, fixture.Outcomes.LootCalls);
    }

    /// <summary>Two pilots but three runs between them: below n_eff 5 there is no number, so the own runs stand in.</summary>
    [AvaloniaFact]
    public async Task EnoughPilots_ButTooFewRuns_FallsBackWithTheRightReason()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ => Scripted.Shared(Sanctum, count: 3, pilots: 2);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.TooFewSharedRuns, attempt.Reason);
    }

    /// <summary>The server's weights are the per-pilot cap already applied: eight rows at half a run each are four runs, not
    /// eight. Counter-proof: weigh them as eight pilots' runs and the panel shows a number from four.</summary>
    [AvaloniaFact]
    public async Task TheServersWeights_AreUsed_NotRecounted()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ => Scripted.Shared(Sanctum, count: 8, pilots: 2, weight: 0.5);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.TooFewSharedRuns, attempt.Reason);
    }

    [AvaloniaFact]
    public async Task AnOutcomeTheServerGivesNoWeight_DoesNotCount()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ => Scripted.Shared(Sanctum, count: 6, pilots: 3, unweighted: 4);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Equal(OwnRunsReason.TooFewSharedRuns, attempt.Reason);
    }

    [AvaloniaFact]
    public async Task Under5RunsIn90Days_TheYearIsReadAndLabelledOlderData()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = since => since >= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-91))
            ? Scripted.Shared(Sanctum, count: 3, pilots: 2)
            : Scripted.Shared(Sanctum, count: 7, pilots: 4);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        ExpectedEarnings shared = Assert.IsType<ExpectedEarnings>(attempt.Shared);
        Assert.Equal(EarningsWindow.Extended, shared.Figures.Window);
        Assert.Equal(4, shared.PilotCount);
        Assert.Equal(2, fixture.Outcomes.OutcomeCalls.Count);
    }

    [AvaloniaFact]
    public async Task AServerThatCannotAnswer_IsAReasonAndNotAnError()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Unreachable = true;

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.ServerUnavailable, attempt.Reason);
    }

    [AvaloniaFact]
    public async Task APilotNotConnectedToTheServer_IsNotAskedFor()
    {
        using Fixture fixture = await Fixture.CreateAsync(connected: false);

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Equal(OwnRunsReason.ServerUnavailable, attempt.Reason);
        Assert.Empty(fixture.Outcomes.OutcomeCalls);
    }

    /// <summary>A share landing between the two reads shifts the rows: loot lines of one outcome must never be added to
    /// another's ISK, so the figure is dropped.</summary>
    [AvaloniaFact]
    public async Task RowsThatChangedBetweenTheTwoReads_GiveNoFigure()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Outcomes.Script = _ =>
        {
            (ActivityOutcomesDto outcomes, ActivityOutcomeLootDto loot) = Scripted.Shared(Sanctum, count: 6, pilots: 3);
            return (outcomes, Scripted.DroppingTheLast(loot));
        };

        SharedEarningsAttempt attempt = await fixture.Service.LoadSharedAsync(Sanctum, Pilot, Token);

        Assert.Null(attempt.Shared);
        Assert.Equal(OwnRunsReason.ServerUnavailable, attempt.Reason);
    }

    [AvaloniaFact]
    public async Task OwnRuns_WithNoRunsSaved_GiveNoFigure()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        ExpectedEarnings own = await fixture.Service.LoadOwnAsync(Sanctum, Token);

        Assert.Equal(EarningsBasis.YourRuns, own.Basis);
        Assert.Equal(EarningsConfidence.None, own.Figures.Confidence);
        Assert.Null(own.Figures.Typical);
    }

    [Fact]
    public void TheWire_MapsTooFewPilots_WithNoOutcomes()
    {
        ActivityOutcomes wire = new() { PilotCount = 1, Availability = (int)SharedOutcomesAvailability.TooFewPilots };

        ActivityOutcomesDto read = ServerRunOutcomeClient.FromWire(Sanctum, wire);

        Assert.Equal(SharedOutcomesAvailability.TooFewPilots, read.Availability);
        Assert.Equal(1, read.PilotCount);
        Assert.Empty(read.Outcomes);
        Assert.Equal(Sanctum, read.Key);
    }

    [Fact]
    public void TheWire_MapsAnOutcomeAndItsLootField_ByField()
    {
        ActivityOutcomes wire = new() { PilotCount = 3, Availability = (int)SharedOutcomesAvailability.Shared };
        wire.Outcomes.Add(new RunOutcomeRow
        {
            CompletedOn = "2026-10-12", DurationSeconds = 754, FixedIsk = "12500000.5", FleetSize = 2,
            LootBasis = (int)LootBasis.CargoDifference, OpenUnrecognisedLootCount = 1, Weight = 0.5
        });
        GetOutcomeLootReply lootWire = new() { PilotCount = 3, Availability = (int)SharedOutcomesAvailability.Shared };
        RunOutcomeLoot outcome = new() { CompletedOn = "2026-10-12", FleetSize = 2, LootBasis = (int)LootBasis.CargoDifference, Weight = 0.5 };
        outcome.Lines.Add(new EveUtils.Grpc.RunOutcomeLootLine { Kind = (int)RunOutcomeLootKind.Mined, TypeId = 34, Quantity = 9_000_000_000 });
        lootWire.Outcomes.Add(outcome);

        RunOutcomeDto row = Assert.Single(ServerRunOutcomeClient.FromWire(Sanctum, wire).Outcomes);
        RunOutcomeLootDto loot = Assert.Single(ServerRunOutcomeClient.FromWire(lootWire).Outcomes);

        Assert.Equal(new DateOnly(2026, 10, 12), row.CompletedOn);
        Assert.Equal(754, row.DurationSeconds);
        Assert.Equal(12_500_000.5m, row.FixedIsk);
        Assert.Equal(0.5, row.Weight);
        Assert.Equal(LootBasis.CargoDifference, row.LootBasis);
        Assert.Equal(RunOutcomeLootKind.Mined, Assert.Single(loot.Lines).Kind);
        Assert.Equal(9_000_000_000, loot.Lines[0].Quantity);
    }

    [Fact]
    public void TheWire_ReadsNoDuration_AsNone()
    {
        ActivityOutcomes wire = new() { Availability = (int)SharedOutcomesAvailability.Shared };
        wire.Outcomes.Add(new RunOutcomeRow { CompletedOn = "2026-10-12", FixedIsk = "1", FleetSize = 1, Weight = 1 });

        Assert.Null(ServerRunOutcomeClient.FromWire(Sanctum, wire).Outcomes[0].DurationSeconds);
    }

    private static ExpectedEarnings Shared(string server, int runs)
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
        private readonly TestClientInstance _instance;

        private Fixture(TestClientInstance instance, ScriptedOutcomeServer outcomes)
        {
            _instance = instance;
            Outcomes = outcomes;
        }

        public ScriptedOutcomeServer Outcomes { get; }
        public ExpectedEarningsService Service => _instance.Services.GetRequiredService<ExpectedEarningsService>();

        public static async Task<Fixture> CreateAsync(bool couple = true, bool optedIn = true, bool connected = true, bool secondServer = false)
        {
            var outcomes = new ScriptedOutcomeServer();
            var connector = new FakeRemoteBusConnector();
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteBusConnector>(connector);
                services.AddSingleton<IServerRunOutcomeClient>(outcomes);
            });
            if (couple)
                await instance.Services.GetRequiredService<IClientSessionStore>()
                    .SaveAsync(Server, new ClientSessionTokens("access", "refresh", "Jithran", Pilot), Token);
            if (secondServer)
            {
                await instance.Services.GetRequiredService<IClientSessionStore>()
                    .SaveAsync(OtherServer, new ClientSessionTokens("access", "refresh", "Jithran", Pilot), Token);
                await instance.Services.GetRequiredService<RunOutcomeSharingChoices>().SetAsync(OtherServer, OutcomeSharingChoice.Shared, Token);
                connector.RaiseCharacterStateChanged(OtherServer, Pilot, ServerConnectionState.Connected);
            }

            if (optedIn)
                await instance.Services.GetRequiredService<RunOutcomeSharingChoices>()
                    .SetAsync(Server, OutcomeSharingChoice.Shared, Token);
            if (connected)
                connector.RaiseCharacterStateChanged(Server, Pilot, ServerConnectionState.Connected);
            return new Fixture(instance, outcomes);
        }

        public void Dispose() => _instance.Dispose();
    }

    private sealed class ScriptedOutcomeServer : IServerRunOutcomeClient
    {
        public Func<DateOnly, (ActivityOutcomesDto, ActivityOutcomeLootDto)> Script { get; set; } =
            _ => Scripted.TooFewPilots(Sanctum, pilots: 0);

        public Dictionary<string, Func<DateOnly, (ActivityOutcomesDto, ActivityOutcomeLootDto)>> PerServer { get; } = [];
        public bool Unreachable { get; set; }
        public List<DateOnly> OutcomeCalls { get; } = [];
        public int LootCalls { get; private set; }

        private (ActivityOutcomesDto, ActivityOutcomeLootDto) ScriptFor(string server, DateOnly since) =>
            (PerServer.TryGetValue(server, out var script) ? script : Script)(since);

        public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
            RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            OutcomeCalls.Add(since);
            return Task.FromResult(Unreachable
                ? OutcomeReadResult<ActivityOutcomesDto>.Transient("down")
                : new OutcomeReadResult<ActivityOutcomesDto>(ScriptFor(serverAddress, since).Item1, "", ""));
        }

        public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            LootCalls++;
            return Task.FromResult(new OutcomeReadResult<ActivityOutcomeLootDto>(ScriptFor(serverAddress, since).Item2, "", ""));
        }

        public Task<OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>> ListOutcomeKeysAsync(string serverAddress,
            long characterId, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>([], "", ""));
    }

    private static class Scripted
    {
        public static (ActivityOutcomesDto, ActivityOutcomeLootDto) Shared(ActivityStatsKey key, int count, int pilots, double weight = 1, int unweighted = 0) =>
            (new ActivityOutcomesDto
            {
                Key = key, Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
                Outcomes = [.. Enumerable.Range(1, count).Select(index => new RunOutcomeDto
                {
                    CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)),
                    DurationSeconds = 600 + index * 60,
                    FixedIsk = index * 10_000_000m,
                    FleetSize = 1,
                    LootBasis = LootBasis.CargoDifference,
                    OpenUnrecognisedLootCount = 0,
                    Weight = index <= unweighted ? 0 : weight
                })]
            }, new ActivityOutcomeLootDto
            {
                Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
                Outcomes = [.. Enumerable.Range(1, count).Select(index => new RunOutcomeLootDto
                {
                    CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)),
                    FleetSize = 1,
                    LootBasis = LootBasis.CargoDifference,
                    Weight = index <= unweighted ? 0 : weight,
                    Lines = []
                })]
            });

        public static (ActivityOutcomesDto, ActivityOutcomeLootDto) TooFewPilots(ActivityStatsKey key, int pilots) =>
            (new ActivityOutcomesDto { Key = key, Availability = SharedOutcomesAvailability.TooFewPilots, PilotCount = pilots, Outcomes = [] },
                new ActivityOutcomeLootDto { Availability = SharedOutcomesAvailability.TooFewPilots, PilotCount = pilots, Outcomes = [] });

        public static ActivityOutcomeLootDto DroppingTheLast(ActivityOutcomeLootDto loot) => new()
        {
            Availability = loot.Availability, PilotCount = loot.PilotCount, Outcomes = [.. loot.Outcomes.SkipLast(1)]
        };
    }
}
