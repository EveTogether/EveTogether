using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunGroupCodeTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_UsesReadableGroupCodeFormat()
    {
        string code = RunGroupCode.Create();

        Assert.Matches("^HF-[A-Z0-9]{4}$", code);
    }

    [Fact]
    public void AbyssalStarts_SameSecond_ConvergeOnOneCode()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc.AddMilliseconds(100));
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc.AddMilliseconds(900));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void AbyssalStarts_SameMillisecond_ConvergeDeterministically()
    {
        RunGroupCodeCandidate firstClient = new("HF-7QK2", StartedAtUtc);
        RunGroupCodeCandidate secondClient = new("HF-1A2B", StartedAtUtc);

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, [firstClient, secondClient]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-1A2B", result.Value);
    }

    [Fact]
    public void HomefrontStarts_UsesCommanderCode()
    {
        RunGroupCodeCandidate commander = new("HF-7QK2", StartedAtUtc, true);
        RunGroupCodeCandidate member = new("HF-1A2B", StartedAtUtc.AddMilliseconds(-1));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Site, [member, commander]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void HomefrontStarts_WithoutCommander_UsesEarliestCode()
    {
        RunGroupCodeCandidate first = new("HF-7QK2", StartedAtUtc);
        RunGroupCodeCandidate second = new("HF-1A2B", StartedAtUtc.AddMilliseconds(1));

        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Site, [second, first]);

        Assert.True(result.IsSuccess);
        Assert.Equal("HF-7QK2", result.Value);
    }

    [Fact]
    public void Select_WithoutCandidates_ReturnsFailure()
    {
        Result<string> result = RunGroupCodeArbiter.Select(ActivityKind.Abyssal, []);

        Assert.False(result.IsSuccess);
    }

    [AvaloniaFact]
    public async Task AbyssalStarts_SameSecond_OnTwoClients_ConvergeOnOneCode()
    {
        var firstWire = new FleetWireTransport();
        var secondWire = new FleetWireTransport();
        using var firstClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(firstWire));
        using var secondClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(secondWire));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IDispatcher firstDispatcher = firstClient.Services.GetRequiredService<IDispatcher>();
        IDispatcher secondDispatcher = secondClient.Services.GetRequiredService<IDispatcher>();
        firstWire.Destination = secondClient.Services.GetRequiredService<IEventBus>();
        secondWire.Destination = firstClient.Services.GetRequiredService<IEventBus>();
        _ = firstClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        _ = secondClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();

        Task<Result<Guid>> firstStarted = firstDispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(100), 0, null, null, FleetId: 42), cancellationToken);
        Task<Result<Guid>> secondStarted = secondDispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(900), 0, null, null, FleetId: 42), cancellationToken);
        Result<Guid>[] started = await Task.WhenAll(firstStarted, secondStarted);

        Assert.All(started, result => Assert.True(result.IsSuccess));
        await using ClientDbContext firstDb = await firstClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        await using ClientDbContext secondDb = await secondClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        string? firstCode = Assert.Single(await firstDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode;
        string? secondCode = Assert.Single(await secondDb.Set<Run>().ToListAsync(cancellationToken)).GroupCode;
        Assert.NotNull(firstCode);
        Assert.Equal(firstCode, secondCode);
    }

    [AvaloniaFact]
    public async Task LinkRunToGroupCode_LateRun_PreservesOwnStartTime()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime lateArrivalUtc = StartedAtUtc.AddMinutes(4);

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Abyssal, lateArrivalUtc,
            0, null, null), cancellationToken);
        Result linked = await dispatcher.Send(new LinkRunToGroupCodeCommand(started.Value, "HF-7QK2"), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(linked.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal("HF-7QK2", run.GroupCode);
        Assert.Equal(lateArrivalUtc, run.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task AbyssalLateArrival_TakesRunningCodeAndPreservesOwnStartTime()
    {
        var firstWire = new FleetWireTransport();
        var secondWire = new FleetWireTransport();
        using var firstClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(firstWire));
        using var secondClient = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(secondWire));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        firstWire.Destination = secondClient.Services.GetRequiredService<IEventBus>();
        secondWire.Destination = firstClient.Services.GetRequiredService<IEventBus>();
        _ = firstClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        _ = secondClient.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        DateTime lateArrivalUtc = StartedAtUtc.AddMinutes(4);

        Result<Guid> firstStarted = await firstClient.Services.GetRequiredService<IDispatcher>().Send(
            new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc, 0, null, null, FleetId: 42), cancellationToken);
        Result<Guid> secondStarted = await secondClient.Services.GetRequiredService<IDispatcher>().Send(
            new StartRunCommand(90000002, ActivityKind.Abyssal, lateArrivalUtc, 0, null, null, FleetId: 42), cancellationToken);

        Assert.True(firstStarted.IsSuccess);
        Assert.True(secondStarted.IsSuccess);
        await using ClientDbContext firstDb = await firstClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        await using ClientDbContext secondDb = await secondClient.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run firstRun = Assert.Single(await firstDb.Set<Run>().ToListAsync(cancellationToken));
        Run secondRun = Assert.Single(await secondDb.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(firstRun.GroupCode, secondRun.GroupCode);
        Assert.Equal(lateArrivalUtc, secondRun.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task HomefrontStarts_ConsecutiveRuns_KeepSeparateCodes()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        _ = instance.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> firstStarted = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, FleetId: 42, IsFleetCommander: true), cancellationToken);
        Result firstSaved = await dispatcher.Send(new SaveRunCommand(firstStarted.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        Result<Guid> secondStarted = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc.AddHours(1), 1234, "Homefront", 30000142, FleetId: 42, IsFleetCommander: true), cancellationToken);

        Assert.True(firstStarted.IsSuccess);
        Assert.True(firstSaved.IsSuccess);
        Assert.True(secondStarted.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>().OrderBy(run => run.StartedAtUtc).ToListAsync(cancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.NotNull(runs[0].GroupCode);
        Assert.NotNull(runs[1].GroupCode);
        Assert.NotEqual(runs[0].GroupCode, runs[1].GroupCode);
    }

    [AvaloniaFact]
    public async Task UnlinkRunFromGroupCode_CorrectedParticipant_PreservesRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);

        Result unlinked = await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(started.Value), cancellationToken);

        Assert.True(unlinked.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Null(run.GroupCode);
        Assert.Equal(StartedAtUtc, run.StartedAtUtc);
    }

    [AvaloniaFact]
    public async Task UnlinkRunFromGroupCode_CorrectedParticipant_ChangesBountyPerParticipant()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid participantRunId = await _SaveRunAsync(dispatcher, 90000001, 100m, cancellationToken);
        Guid stationRunId = await _SaveRunAsync(dispatcher, 90000002, 0m, cancellationToken);

        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        ActivitySummary beforeCorrection = Assert.Single(await db.Set<ActivitySummary>().AsNoTracking().ToListAsync(cancellationToken));
        decimal beforePerParticipant = beforeCorrection.BountyIsk / beforeCorrection.ParticipantCount;

        Result unlinked = await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(stationRunId), cancellationToken);
        Result<int> rebuilt = await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        ActivitySummary afterCorrection = Assert.Single(await db.Set<ActivitySummary>().AsNoTracking()
            .Where(summary => summary.GroupCode == "HF-7QK2")
            .ToListAsync(cancellationToken));
        decimal afterPerParticipant = afterCorrection.BountyIsk / afterCorrection.ParticipantCount;

        Assert.True(unlinked.IsSuccess);
        Assert.True(rebuilt.IsSuccess);
        Assert.Equal(50m, beforePerParticipant);
        Assert.Equal(100m, afterPerParticipant);
        Assert.NotEqual(beforePerParticipant, afterPerParticipant);
        Assert.NotEqual(participantRunId, stationRunId);
    }

    [AvaloniaFact]
    public async Task LinkRunToGroupCode_ExistingDifferentCode_Fails()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            0, null, null, "HF-7QK2"), cancellationToken);

        Result linked = await dispatcher.Send(new LinkRunToGroupCodeCommand(started.Value, "HF-1A2B"), cancellationToken);

        Assert.False(linked.IsSuccess);
    }

    /// <summary>
    /// The shape ET-136 is about: one fleet, three pilots, not all of them on the same rock. The commander and the
    /// member beside him share his group code; the member running his own site a jump away must not be swept into
    /// it, and before the site entered the key he was.
    /// </summary>
    [AvaloniaFact]
    public async Task SiteStarts_MemberElsewhere_KeepsOutOfTheCommandersGroup()
    {
        await using FleetOfThree fleet = FleetOfThree.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> commander = await fleet.Commander.Dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc, 0, "Sansha's Refuge", 30000142, FleetId: FleetId, IsFleetCommander: true,
            SolarSystemName: "Jita"), cancellationToken);
        Result<Guid> beside = await fleet.Beside.Dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site,
            StartedAtUtc.AddSeconds(20), 0, "Sansha's Refuge", 30000142, FleetId: FleetId,
            SolarSystemName: "Jita"), cancellationToken);
        Result<Guid> elsewhere = await fleet.Elsewhere.Dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site,
            StartedAtUtc.AddSeconds(40), 0, "Serpentis Hideaway", 30002187, FleetId: FleetId,
            SolarSystemName: "Amarr"), cancellationToken);

        Assert.True(commander.IsSuccess);
        Assert.True(beside.IsSuccess);
        Assert.True(elsewhere.IsSuccess);
        string? commanderCode = await fleet.Commander.GroupCodeAsync(cancellationToken);
        Assert.NotNull(commanderCode);
        Assert.Equal(commanderCode, await fleet.Beside.GroupCodeAsync(cancellationToken));
        Assert.Null(await fleet.Elsewhere.GroupCodeAsync(cancellationToken));
    }

    /// <summary>
    /// The same split the other way round in time: the member was already flying his own site when the commander
    /// started. His run must not be relinked to a group he was never in — that retroactive link is what made one
    /// DISCARD take runs with it that were never part of the run being ended.
    /// </summary>
    [AvaloniaFact]
    public async Task SiteStarts_RunAlreadyRunningElsewhere_IsNotRelinkedByTheCommandersStart()
    {
        await using FleetOfThree fleet = FleetOfThree.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> elsewhere = await fleet.Elsewhere.Dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site,
            StartedAtUtc, 0, "Serpentis Hideaway", 30002187, FleetId: FleetId,
            SolarSystemName: "Amarr"), cancellationToken);
        Result<Guid> commander = await fleet.Commander.Dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            StartedAtUtc.AddMinutes(2), 0, "Sansha's Refuge", 30000142, FleetId: FleetId, IsFleetCommander: true,
            SolarSystemName: "Jita"), cancellationToken);

        Assert.True(elsewhere.IsSuccess);
        Assert.True(commander.IsSuccess);
        Assert.NotNull(await fleet.Commander.GroupCodeAsync(cancellationToken));
        Assert.Null(await fleet.Elsewhere.GroupCodeAsync(cancellationToken));
    }

    /// <summary>Two abyssal runs that would have converged on one code the moment they were in the same fleet and
    /// the same second. Different systems are different runs, so they keep the codes they made.</summary>
    [AvaloniaFact]
    public async Task AbyssalStarts_SameSecond_InDifferentSystems_KeepSeparateCodes()
    {
        await using FleetOfThree fleet = FleetOfThree.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> first = await fleet.Commander.Dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(100), 0, null, 30000142, FleetId: FleetId,
            SolarSystemName: "Jita"), cancellationToken);
        Result<Guid> second = await fleet.Elsewhere.Dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Abyssal,
            StartedAtUtc.AddMilliseconds(900), 0, null, 30002187, FleetId: FleetId,
            SolarSystemName: "Amarr"), cancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        string? firstCode = await fleet.Commander.GroupCodeAsync(cancellationToken);
        string? secondCode = await fleet.Elsewhere.GroupCodeAsync(cancellationToken);
        Assert.NotNull(firstCode);
        Assert.NotNull(secondCode);
        Assert.NotEqual(firstCode, secondCode);
    }

    private const long FleetId = 42;

    /// <summary>Three clients on one fleet wire, each with its own store and its own coordinator running.</summary>
    private sealed class FleetOfThree : IAsyncDisposable
    {
        private FleetOfThree(FleetPilot commander, FleetPilot beside, FleetPilot elsewhere)
        {
            Commander = commander;
            Beside = beside;
            Elsewhere = elsewhere;
        }

        public FleetPilot Commander { get; }

        public FleetPilot Beside { get; }

        public FleetPilot Elsewhere { get; }

        public static FleetOfThree Create()
        {
            FleetWireTransport[] wires = [new(), new(), new()];
            FleetPilot[] pilots = [.. wires.Select(wire =>
                new FleetPilot(TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(wire))))];
            for (int sender = 0; sender < pilots.Length; sender++)
                foreach (FleetPilot other in pilots.Where((_, index) => index != sender))
                    wires[sender].Destinations.Add(other.Instance.Services.GetRequiredService<IEventBus>());

            return new FleetOfThree(pilots[0], pilots[1], pilots[2]);
        }

        public ValueTask DisposeAsync()
        {
            Commander.Instance.Dispose();
            Beside.Instance.Dispose();
            Elsewhere.Instance.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FleetPilot
    {
        public FleetPilot(TestClientInstance instance)
        {
            Instance = instance;
            Dispatcher = instance.Services.GetRequiredService<IDispatcher>();
            _ = instance.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        }

        public TestClientInstance Instance { get; }

        public IDispatcher Dispatcher { get; }

        /// <summary>The group code on this pilot's own single run — the only run in this pilot's own store.</summary>
        public async Task<string?> GroupCodeAsync(CancellationToken cancellationToken)
        {
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
                .CreateDbContextAsync(cancellationToken);
            return Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken)).GroupCode;
        }
    }

    private sealed class FleetWireTransport : IRemoteEventTransport
    {
        /// <summary>Every other client in the fleet. A fleet is more than two pilots, and the bugs this file covers
        /// only show up once a third one is somewhere else.</summary>
        public List<IEventBus> Destinations { get; } = [];

        public IEventBus? Destination
        {
            set
            {
                Destinations.Clear();
                if (value is not null)
                    Destinations.Add(value);
            }
        }

        public async Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            foreach (IEventBus destination in Destinations)
                await destination.PublishAsync(integrationEvent, EventTarget.Local, cancellationToken);
        }
    }

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher, long characterId, decimal bountyIsk,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [new RunBountyEntryInput
            {
                OccurredAtUtc = StartedAtUtc.AddMinutes(5),
                Isk = bountyIsk
            }], [], []), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }
}
