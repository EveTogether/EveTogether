using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-519: where the loot table's figures come from. A server's shared outcomes when the pilot opted in and at least two
/// pilots share the activity; the pilot's own runs otherwise, with the reason and no error. The server is a double that
/// answers per call, so every refusal and the cache can be told apart.
/// </summary>
public sealed class LootTableServiceTests
{
    private const int Pilot = 90000001;
    private const string Address = "https://krah.invalid";
    private const int Tag = 100;
    private static readonly ActivityStatsKey Sanctum = LootTableViewModelTests.Sanctum;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static LootTableRequest Request(EarningsBasis basis = EarningsBasis.AllPilots, LootTableWindow window = LootTableWindow.Days90) =>
        new(Sanctum, basis, window, [Pilot]);

    [AvaloniaFact]
    public async Task TwoPilotsSharing_GiveTheSharedTable()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Script = Scripted.Shared(runs: 20, pilots: 2);

        LootTableReading reading = await fixture.Source.ReadAsync(Request(), Token);

        Assert.Equal(EarningsBasis.AllPilots, reading.Basis);
        Assert.Equal(OwnRunsReason.None, reading.Reason);
        Assert.Equal(LootTableConfidence.Rated, reading.Table.Confidence);
        LootTableItemDto item = Assert.Single(reading.Table.Items);
        Assert.Equal(Tag, item.TypeId);
        Assert.Equal(1d, item.DropRate, 9);
        Assert.Equal(1, fixture.Server.LootCalls);
    }

    /// <summary>One pilot on the server is below the minimum of two: the server answers without outcomes and the table is the
    /// pilot's own runs with the reason, and no loot is asked for. Counter-proof: drop the check on the availability and the
    /// empty shared list is shown as the table.</summary>
    [AvaloniaFact]
    public async Task TooFewPilots_FallBackToTheOwnRuns_WithTheReason_AndReadNoLoot()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Script = Scripted.TooFewPilots(pilots: 1);

        LootTableReading reading = await fixture.Source.ReadAsync(Request(), Token);

        Assert.Equal(EarningsBasis.YourRuns, reading.Basis);
        Assert.Equal(OwnRunsReason.TooFewPilots, reading.Reason);
        Assert.Null(reading.ServerName);
        Assert.Equal(0, fixture.Server.LootCalls);
        Assert.Equal(LootTableConfidence.None, reading.Table.Confidence);
    }

    [AvaloniaFact]
    public async Task NotOptedIn_ReadsTheOwnRuns_AndNeverAsksTheServer()
    {
        using Fixture fixture = await Fixture.CreateAsync(optedIn: false);

        LootTableReading reading = await fixture.Source.ReadAsync(Request(), Token);

        Assert.Equal(EarningsBasis.YourRuns, reading.Basis);
        Assert.Equal(OwnRunsReason.NotSharing, reading.Reason);
        Assert.Equal(0, fixture.Server.OutcomeCalls);
    }

    [AvaloniaFact]
    public async Task AServerThatIsDown_ReadsTheOwnRuns_WithoutThrowing()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Unreachable = true;

        LootTableReading reading = await fixture.Source.ReadAsync(Request(), Token);

        Assert.Equal(EarningsBasis.YourRuns, reading.Basis);
        Assert.Equal(OwnRunsReason.ServerUnavailable, reading.Reason);
    }

    [AvaloniaFact]
    public async Task YourRuns_NeverAsksTheServer()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Script = Scripted.Shared(runs: 20, pilots: 3);

        LootTableReading reading = await fixture.Source.ReadAsync(Request(EarningsBasis.YourRuns), Token);

        Assert.Equal(EarningsBasis.YourRuns, reading.Basis);
        Assert.Equal(0, fixture.Server.OutcomeCalls);
    }

    /// <summary>The server is read once over the long window: switching the window is a new table out of the same outcomes.
    /// Counter-proof: leave the cache out and the second read is a second call.</summary>
    [AvaloniaFact]
    public async Task SwitchingTheWindow_IsANewTableOutOfTheSameRead()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Script = Scripted.Shared(runs: 20, pilots: 3);

        LootTableReading ninety = await fixture.Source.ReadAsync(Request(window: LootTableWindow.Days90), Token);
        LootTableReading thirty = await fixture.Source.ReadAsync(Request(window: LootTableWindow.Days30), Token);

        Assert.Equal(1, fixture.Server.OutcomeCalls);
        Assert.Equal(1, fixture.Server.LootCalls);
        Assert.Equal(LootTableWindow.Days30, thirty.Table.Window);
        Assert.Equal(20, ninety.Table.RunCount);
    }

    [AvaloniaFact]
    public async Task WithoutACatalogueOrARun_NoSiteIsListed()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        Assert.Empty(await fixture.Source.ListSitesAsync(Token));
    }

    /// <summary>ET-521: the picker offers every site of the catalogue, the one flown first with its own name, the others by
    /// name with no "last flown"; a twin pair is one site, not two. Counter-proof: list only what was flown and the two
    /// unflown sites are missing.</summary>
    [AvaloniaFact]
    public async Task EverySiteOfTheCatalogue_IsListed_TheFlownOneFirst_AndTwinsOnce()
    {
        FakeSdeAccessor sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, []))
            .AddSite(new SdeSite(2400, "Superior Sleeper Cache", 1, null, null, null, null, null, false, []))
            .AddSite(new SdeSite(2401, "Superior Sleeper Cache", 1, null, null, null, null, null, false, []))
            .AddSite(new SdeSite(2316, "Crimson Cache", 1, null, null, null, null, null, false, []));
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(sde));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        DateTime startedAtUtc = DateTime.UtcNow.AddDays(-2);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 2316, "Crimson Cache", 30000142), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16), [], [], [], []), Token);

        IReadOnlyList<LootSiteChoice> sites = await instance.Services.GetRequiredService<ILootTableSource>().ListSitesAsync(Token);

        Assert.Equal([2316, 2311, 2400], sites.Select(site => site.Key.TypeId));
        Assert.NotNull(sites[0].LastFlownUtc);
        Assert.Equal("Crimson Cache", sites[0].SiteName);
        Assert.All(sites.Skip(1), site => Assert.Null(site.LastFlownUtc));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestClientInstance _instance;

        private Fixture(TestClientInstance instance, ScriptedServer server)
        {
            _instance = instance;
            Server = server;
        }

        public ScriptedServer Server { get; }

        public ILootTableSource Source => _instance.Services.GetRequiredService<ILootTableSource>();

        public static async Task<Fixture> CreateAsync(bool optedIn = true)
        {
            var server = new ScriptedServer();
            var connector = new FakeRemoteBusConnector();
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteBusConnector>(connector);
                services.AddSingleton<IServerRunOutcomeClient>(server);
            });
            await instance.Services.GetRequiredService<IClientSessionStore>()
                .SaveAsync(Address, new ClientSessionTokens("access", "refresh", "Jithran", Pilot), Token);
            if (optedIn)
                await instance.Services.GetRequiredService<RunOutcomeSharingChoices>().SetAsync(Address, OutcomeSharingChoice.Shared, Token);
            connector.RaiseCharacterStateChanged(Address, Pilot, ServerConnectionState.Connected);
            return new Fixture(instance, server);
        }

        public void Dispose() => _instance.Dispose();
    }

    private sealed class ScriptedServer : IServerRunOutcomeClient
    {
        public (ActivityOutcomesDto Outcomes, ActivityOutcomeLootDto Loot) Script { get; set; } = Scripted.TooFewPilots(pilots: 0);
        public bool Unreachable { get; set; }
        public int OutcomeCalls { get; private set; }
        public int LootCalls { get; private set; }

        public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
            RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            OutcomeCalls++;
            return Task.FromResult(Unreachable
                ? OutcomeReadResult<ActivityOutcomesDto>.Transient("down")
                : new OutcomeReadResult<ActivityOutcomesDto>(Script.Outcomes, "", ""));
        }

        public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default)
        {
            LootCalls++;
            return Task.FromResult(new OutcomeReadResult<ActivityOutcomeLootDto>(Script.Loot, "", ""));
        }

        public Task<OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>> ListOutcomeKeysAsync(string serverAddress,
            long characterId, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>([], "", ""));
    }

    private static class Scripted
    {
        public static (ActivityOutcomesDto, ActivityOutcomeLootDto) Shared(int runs, int pilots) =>
            (new ActivityOutcomesDto
            {
                Key = Sanctum, Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
                Outcomes = [.. Enumerable.Range(1, runs).Select(index => new RunOutcomeDto
                {
                    CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)), DurationSeconds = 600, FixedIsk = 1_000_000m,
                    FleetSize = 1, LootBasis = LootBasis.CargoDifference, OpenUnrecognisedLootCount = 0, Weight = 1
                })]
            }, new ActivityOutcomeLootDto
            {
                Availability = SharedOutcomesAvailability.Shared, PilotCount = pilots,
                Outcomes = [.. Enumerable.Range(1, runs).Select(index => new RunOutcomeLootDto
                {
                    CompletedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index)), FleetSize = 1, LootBasis = LootBasis.CargoDifference,
                    Weight = 1, Lines = [new RunOutcomeLootLineDto { Kind = RunOutcomeLootKind.Gained, TypeId = Tag, Quantity = 3 }]
                })]
            });

        public static (ActivityOutcomesDto, ActivityOutcomeLootDto) TooFewPilots(int pilots) =>
            (new ActivityOutcomesDto { Key = Sanctum, Availability = SharedOutcomesAvailability.TooFewPilots, PilotCount = pilots, Outcomes = [] },
                new ActivityOutcomeLootDto { Availability = SharedOutcomesAvailability.TooFewPilots, PilotCount = pilots, Outcomes = [] });
    }
}
