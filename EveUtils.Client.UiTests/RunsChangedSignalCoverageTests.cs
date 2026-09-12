using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Data;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-222: the guard that keeps the screens current. Every screen that shows runs listens to one signal,
/// <see cref="RunsChangedEvent"/>, so a command that changes a run and forgets to publish it leaves every one of them
/// stale — the exact gap ET-189, ET-203 and ET-220 each had to close by hand for one command at a time.
///
/// Two halves. The first finds every command the Runs module handles, by reflection, and fails for one that has
/// neither a scenario below nor an entry on the exemption list: writing a new command means deciding here which it
/// is. The second runs each scenario against a real store and the real bus and fails when the command did not
/// publish the signal for the run it changed. So a new command cannot slip past unseen, and one with a scenario
/// cannot forget to signal.
/// </summary>
public sealed class RunsChangedSignalCoverageTests
{
    private const long Pilot = 90000001;
    private const string GroupCode = "HF-7QK2";
    private const string ServerAddress = "https://server.invalid";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Commands in the Runs module that change nothing a screen shows about a run, each with the reason. Empty
    /// today: every run command there changes something the runs screen, the dashboard or the detail screen reads.
    /// An entry here is a claim a reviewer has to agree with, not a way to make this test pass.</summary>
    private static readonly IReadOnlyDictionary<Type, string> Exempt = new Dictionary<Type, string>
    {
        // ET-254: writes Run.LastAliveAtUtc, a field no screen shows at all — it exists only for
        // StopRunsLeftRunningCommandHandler to read back at the next startup. Publishing RunsChangedEvent for it
        // would mean every screen showing runs redrawing once a minute, for every open run window, for a change
        // none of them can display.
        [typeof(TouchRunAliveCommand)] = "writes a field (LastAliveAtUtc) that exists only for the next startup's "
            + "sweep to read, never shown on any screen — a signal for it would be a redraw nobody can see the point of",
        // ET-245: writes RunGroupOrigin.ServerAddress, which no screen shows — only FleetRunAutoPublisher reads it, to
        // know where a fleet run goes. It never touches a run, and the publisher itself sends it from inside its own
        // handling of RunsChangedEvent, so a signal here would only hand that handler its own write back.
        [typeof(RecordRunGroupServerCommand)] = "writes which server a group's fleet lives on, read by the automatic "
            + "publisher alone and shown nowhere; it changes no run",
        // ET-228: its only effect is matching a run's SiteName against the SDE's own archetype-70 site names, and
        // this harness's TestClientInstance.Create() below carries no SDE catalogue at all — every scenario shares
        // one instance with no per-scenario override, so there is no fixture strong enough to make this command do
        // anything here. It does change what a screen shows (TYPE) and does publish RunsChangedEvent through the
        // RebuildActivitySummariesCommand it delegates to once repaired — proven directly, against a FakeSdeAccessor
        // seeded with the site it must find, in RepairHomefrontSiteTypeIdsCommandHandlerTests instead.
        [typeof(RepairHomefrontSiteTypeIdsCommand)] = "needs the SDE's own archetype-70 site catalogue to do "
            + "anything, which this shared harness has no way to seed per scenario; its signal is proven in "
            + "RepairHomefrontSiteTypeIdsCommandHandlerTests against a FakeSdeAccessor instead"
    };

    /// <summary>For every other command: real state to run it against, and the run (or group) it has to name.</summary>
    private static readonly IReadOnlyDictionary<Type, Arrange> Scenarios = new Dictionary<Type, Arrange>
    {
        [typeof(StartRunCommand)] = (dispatcher, cancellationToken) => Task.FromResult(new Act(
            async () => await dispatcher.Send(_Start(GroupCode), cancellationToken), GroupCode: GroupCode)),

        [typeof(SetRunStoppedCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new SetRunStoppedCommand(runId, StartedAtUtc.AddMinutes(5)), cancellationToken), runId);
        },

        [typeof(StopRunsLeftRunningCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new StopRunsLeftRunningCommand(StartedAtUtc.AddHours(1)),
                cancellationToken), runId);
        },

        [typeof(SaveRunCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(_Save(runId), cancellationToken), runId);
        },

        [typeof(SaveRunsLeftUnfinishedCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            await dispatcher.Send(new SetRunStoppedCommand(runId, StartedAtUtc.AddMinutes(5)), cancellationToken);
            return new Act(async () => await dispatcher.Send(new SaveRunsLeftUnfinishedCommand(StartedAtUtc.AddDays(2)),
                cancellationToken), runId);
        },

        [typeof(DiscardRunCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new DiscardRunCommand(runId, StartedAtUtc.AddMinutes(5)), cancellationToken), runId);
        },

        [typeof(DiscardRunsInGroupCommand)] = async (dispatcher, cancellationToken) =>
        {
            await _StartAsync(dispatcher, cancellationToken, GroupCode);
            return new Act(async () => await dispatcher.Send(new DiscardRunsInGroupCommand(GroupCode, StartedAtUtc.AddMinutes(5)),
                cancellationToken), GroupCode: GroupCode);
        },

        [typeof(DeleteRunCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartSavedAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new DeleteRunCommand(runId, StartedAtUtc.AddHours(1)), cancellationToken), runId);
        },

        [typeof(DeleteRunsInGroupCommand)] = async (dispatcher, cancellationToken) =>
        {
            await _StartSavedAsync(dispatcher, cancellationToken, GroupCode);
            return new Act(async () => await dispatcher.Send(new DeleteRunsInGroupCommand(GroupCode, StartedAtUtc.AddHours(1)),
                cancellationToken), GroupCode: GroupCode);
        },

        [typeof(RestoreRunCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartSavedAsync(dispatcher, cancellationToken);
            await dispatcher.Send(new DeleteRunCommand(runId, StartedAtUtc.AddHours(1)), cancellationToken);
            return new Act(() => dispatcher.Send(new RestoreRunCommand(runId), cancellationToken), runId);
        },

        [typeof(RestoreRunsInGroupCommand)] = async (dispatcher, cancellationToken) =>
        {
            await _StartSavedAsync(dispatcher, cancellationToken, GroupCode);
            await dispatcher.Send(new DeleteRunsInGroupCommand(GroupCode, StartedAtUtc.AddHours(1)), cancellationToken);
            return new Act(async () => await dispatcher.Send(new RestoreRunsInGroupCommand(GroupCode), cancellationToken),
                GroupCode: GroupCode);
        },

        [typeof(RebuildActivitySummariesCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartSavedAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken), runId);
        },

        [typeof(LinkRunToGroupCodeCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new LinkRunToGroupCodeCommand(runId, GroupCode), cancellationToken), runId);
        },

        [typeof(UnlinkRunFromGroupCodeCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken, GroupCode);
            return new Act(() => dispatcher.Send(new UnlinkRunFromGroupCodeCommand(runId), cancellationToken), runId);
        },

        [typeof(QueueRunForServerSyncCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartSavedAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new QueueRunForServerSyncCommand(runId, ServerAddress),
                cancellationToken), runId);
        },

        [typeof(AddRunBountyEntryCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new AddRunBountyEntryCommand(Pilot, StartedAtUtc.AddMinutes(2), 250_000m),
                cancellationToken), runId);
        },

        [typeof(AddRunMiningEntryCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(
                new AddRunMiningEntryCommand(Pilot, StartedAtUtc.AddMinutes(2), "Amperum Mutanite", 13, false, 0),
                cancellationToken), runId);
        },

        [typeof(AddRunLootCaptureCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new AddRunLootCaptureCommand(_Capture(runId)), cancellationToken), runId);
        },

        [typeof(SetRunCargoHoldCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new SetRunCargoHoldCommand(runId, LootCaptureRole.CargoBefore,
                StartedAtUtc.AddMinutes(1), [_Entry()]), cancellationToken), runId);
        },

        [typeof(SetRunLootCaptureExclusionCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            Guid captureId = await _CaptureAsync(dispatcher, runId, cancellationToken);
            return new Act(() => dispatcher.Send(new SetRunLootCaptureExclusionCommand(captureId, true), cancellationToken), runId);
        },

        [typeof(SetRunLootCaptureRoleCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            Guid captureId = await _CaptureAsync(dispatcher, runId, cancellationToken);
            return new Act(() => dispatcher.Send(new SetRunLootCaptureRoleCommand(captureId, LootCaptureRole.CargoBefore),
                cancellationToken), runId);
        },

        [typeof(SetRunLootManualCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new SetRunLootManualCommand(runId, StartedAtUtc.AddMinutes(3),
                [_Entry()]), cancellationToken), runId);
        },

        [typeof(SetRunLootStrategyCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new SetRunLootStrategyCommand(runId, RunLootStrategy.AllCans), cancellationToken), runId);
        },

        [typeof(SetRunPayoutEligibilityCommand)] = async (dispatcher, cancellationToken) =>
        {
            Guid runId = await _StartAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new SetRunPayoutEligibilityCommand(runId, false), cancellationToken), runId);
        }
    };

    public static TheoryData<string> CommandsWithAScenario()
    {
        var names = new TheoryData<string>();
        foreach (Type command in Scenarios.Keys)
            names.Add(command.Name);
        return names;
    }

    /// <summary>The reflection half. Counter-proof, measured: a <c>ProbeRunCommand</c> and handler dropped into
    /// <c>Modules/Runs/Commands</c> with no entry here turns this red, naming it.</summary>
    [Fact]
    public void EveryRunCommand_HasAScenarioProvingItSignals_OrAReasonItNeedNot()
    {
        Type[] commands = _RunCommands();
        Assert.NotEmpty(commands);

        Type[] unaccounted = [.. commands.Where(command => !Scenarios.ContainsKey(command) && !Exempt.ContainsKey(command))];
        Assert.True(unaccounted.Length == 0,
            "These run commands have no scenario proving they publish RunsChangedEvent, and no reason on the exemption "
            + $"list why they need not: {string.Join(", ", unaccounted.Select(command => command.Name))}. Add a scenario "
            + "to RunsChangedSignalCoverageTests — and have the handler publish the signal once its write is done.");

        Assert.DoesNotContain(Scenarios.Keys.Concat(Exempt.Keys), listed => !commands.Contains(listed));
        Assert.Empty(Scenarios.Keys.Intersect(Exempt.Keys));
        Assert.All(Exempt, exemption => Assert.False(string.IsNullOrWhiteSpace(exemption.Value)));
    }

    /// <summary>The behaviour half. Counter-proof, measured: take the publish back out of
    /// <c>AddRunBountyEntryCommandHandler</c> — the handler that published nothing before ET-222 — and its case goes
    /// red with no signal at all.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(CommandsWithAScenario))]
    public async Task RunCommand_PublishesRunsChanged_ForTheRunItChanged(string commandName)
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Act act = await Scenarios.Single(scenario => scenario.Key.Name == commandName).Value(dispatcher, cancellationToken);

        List<RunsChangedEventData> signalled = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => signalled.Add(changed.Data));
        Result outcome = await act.Send();

        Assert.True(outcome.IsSuccess, outcome.Messages.FirstOrDefault()?.Text);
        Assert.True(signalled.Count > 0, $"{commandName} changed a run and published no RunsChangedEvent.");
        Assert.Contains(signalled, change =>
            change is { RunId: null, GroupCode: null }
            || act.RunId is { } runId && change.RunId == runId
            || act.GroupCode is { } groupCode && change.GroupCode == groupCode);
    }

    /// <summary>Server synchronisation writes runs without a command, so the reflection half cannot see it — which is
    /// why the ticket calls it the widest gap. A push the server accepted turns the run's "queued" into "published";
    /// the screens hear it for that run. Counter-proof: without the publish beside <c>_MarkSyncedAsync</c> nothing is
    /// signalled at all — there is no pull and no rebuild here to cover for it.</summary>
    [AvaloniaFact]
    public async Task ServerSync_APushTheServerAccepted_SignalsThatRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = await _StartSavedAsync(dispatcher, cancellationToken);
        await dispatcher.Send(new QueueRunForServerSyncCommand(runId, ServerAddress), cancellationToken);
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        var synchronization = new RunSynchronizationService(
            instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>(), new AcceptingSyncClient([]),
            instance.Services.GetRequiredService<RunSynchronizationApplier>(), bus);

        List<RunsChangedEventData> signalled = [];
        using IDisposable listening = bus.Subscribe<RunsChangedEvent>(changed => signalled.Add(changed.Data));
        (bool accepted, _) = await synchronization.SynchronizeAsync(ServerAddress, Pilot, cancellationToken);

        Assert.True(accepted);
        Assert.Contains(signalled, change => change.RunId == runId);
    }

    /// <summary>Every run a pull brings in is signalled by itself and with its group — a group-mate's run arriving is
    /// only recognisable to an open detail screen by the group code. Counter-proof: without the per-run publish in
    /// <c>RunSynchronizationApplier</c> only the rebuild's unscoped signal is left, which names neither run.</summary>
    [AvaloniaFact]
    public async Task ServerSync_EveryRunAPullBringsIn_IsSignalledWithItsGroup()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunWirePayload[] pulled = [_Pulled(90000002), _Pulled(90000003)];

        List<RunsChangedEventData> signalled = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => signalled.Add(changed.Data));
        await instance.Services.GetRequiredService<RunSynchronizationApplier>()
            .ApplyAsync(ServerAddress, pulled, new HashSet<Guid>(), cancellationToken);

        Assert.All(pulled, payload => Assert.Contains(signalled,
            change => change.RunId == payload.Run.Id && change.GroupCode == GroupCode));
    }

    private static RunWirePayload _Pulled(long characterId) => new()
    {
        Run = RunWireData.FromEntity(new EveUtils.Shared.Modules.Runs.Entities.Run
        {
            Id = Guid.CreateVersion7(),
            CharacterId = characterId,
            GroupCode = GroupCode,
            ActivityKind = ActivityKind.Site,
            State = RunState.Saved,
            StartedAtUtc = StartedAtUtc,
            StoppedAtUtc = StartedAtUtc.AddMinutes(15),
            SavedAtUtc = StartedAtUtc.AddMinutes(16),
            SiteTypeId = 1234,
            SiteName = "Homefront",
            Revision = 1
        }),
        SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private static Type[] _RunCommands() =>
    [
        .. typeof(StartRunCommand).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && type.Namespace?.StartsWith("EveUtils.Shared.Modules.Runs", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                               && (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                                   || contract.GetGenericTypeDefinition() == typeof(ICommandHandler<>)))
            .Select(contract => contract.GetGenericArguments()[0])
            .Distinct()
    ];

    private static StartRunCommand _Start(string? groupCode = null) =>
        new(Pilot, ActivityKind.Site, StartedAtUtc, 1234, "Homefront", 30000142, groupCode);

    private static async Task<Guid> _StartAsync(IDispatcher dispatcher, CancellationToken cancellationToken,
        string? groupCode = null)
    {
        Result<Guid> started = await dispatcher.Send(_Start(groupCode), cancellationToken);
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task<Guid> _StartSavedAsync(IDispatcher dispatcher, CancellationToken cancellationToken,
        string? groupCode = null)
    {
        Guid runId = await _StartAsync(dispatcher, cancellationToken, groupCode);
        Assert.True((await dispatcher.Send(_Save(runId), cancellationToken)).IsSuccess);
        return runId;
    }

    private static SaveRunCommand _Save(Guid runId) =>
        new(runId, StartedAtUtc.AddMinutes(10), StartedAtUtc.AddMinutes(11), [], [], [], []);

    private static async Task<Guid> _CaptureAsync(IDispatcher dispatcher, Guid runId, CancellationToken cancellationToken)
    {
        Result<RunLootCaptureSaveResult> captured = await dispatcher.Send(new AddRunLootCaptureCommand(_Capture(runId)),
            cancellationToken);
        return captured.Value?.CaptureId ?? throw new InvalidOperationException("The capture was not stored.");
    }

    private static RunLootCaptureInput _Capture(Guid runId) => new()
    {
        CapturedAtUtc = StartedAtUtc.AddMinutes(4),
        Source = LootCaptureSource.Clipboard,
        PreferredRunId = runId,
        CharacterId = Pilot,
        Entries = [_Entry()]
    };

    private static RunLootEntryInput _Entry() => new()
    {
        ItemTypeId = 34,
        Name = "Tritanium",
        Quantity = 100,
        LootKind = LootKind.Gained
    };

    /// <summary>A server that takes every push and hands back what it was given to pull.</summary>
    private sealed class AcceptingSyncClient(IReadOnlyList<RunWirePayload> pulled) : IServerRunSyncClient
    {
        public Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
            string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "accepted", (DateTime?)DateTime.UtcNow));

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
            string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "accepted", pulled));
    }

    /// <summary>Builds the state a command needs and hands back the command itself, not yet sent.</summary>
    private delegate Task<Act> Arrange(IDispatcher dispatcher, CancellationToken cancellationToken);

    /// <param name="RunId">The run the command changes, when it names one.</param>
    /// <param name="GroupCode">The group it changes, for a command that works on a whole group.</param>
    private sealed record Act(Func<Task<Result>> Send, Guid? RunId = null, string? GroupCode = null);
}
