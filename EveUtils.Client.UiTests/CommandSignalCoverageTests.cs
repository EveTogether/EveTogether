using System.Text.RegularExpressions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Commands;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Gamelog.Commands;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Ships.Commands;
using EveUtils.Shared.Modules.Sync.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-380 (epic ET-379): every command that changes state publishes its module's signal once the write is done, so no
/// screen, relay or other client is left stale. ET-222 proved the rule for Runs alone; this holds every command in
/// <c>EveUtils.Shared</c> to it.
///
/// Two halves. The reflection half finds every command handler and fails for a command that has neither a scenario
/// proving it signals, nor a reason on the exemption list, nor a ticket on the known-gap list: writing a new command
/// means deciding here which it is. The behaviour half runs each scenario against a real store and the real bus and
/// fails when the command did not publish its module's signal for what it changed. The Runs scenarios live in
/// <see cref="RunsChangedSignalCoverageTests"/>, which needs run-specific fixtures; they count here all the same.
/// </summary>
public sealed class CommandSignalCoverageTests
{
    private const int Owner = 95001680;
    private const string ModulesNamespace = "EveUtils.Shared.Modules.";

    /// <summary>The one signal each module (or sub-module) publishes after a write, keyed on the namespace between
    /// <c>Modules.</c> and <c>.Commands</c>. A module missing here has no signal yet: each of its commands is a known gap
    /// or an exemption until it gets one.</summary>
    private static readonly IReadOnlyDictionary<string, Type> Signals = new Dictionary<string, Type>
    {
        ["Runs"] = typeof(RunsChangedEvent),
        ["Fleet"] = typeof(FleetChangedEvent),
        ["Fleet.Composition"] = typeof(CompositionChangedEvent)
    };

    /// <summary>Commands that change nothing anyone has to hear about, each with the reason. An entry here is a claim a
    /// reviewer has to agree with, not a way to make this test pass.</summary>
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
        // ET-228, widened to every archetype by ET-275: its only effect is matching a run's SiteName against the
        // SDE's own site catalogue, which TestClientInstance carries none of. It does publish RunsChangedEvent through
        // the RebuildActivitySummariesCommand it delegates to once repaired — proven against a FakeSdeAccessor seeded
        // with the site it must find, in RepairSiteTypeIdsCommandHandlerTests instead.
        [typeof(RepairSiteTypeIdsCommand)] = "needs the SDE's own site catalogue to do anything, which the shared "
            + "harness has no way to seed per scenario; its signal is proven in RepairSiteTypeIdsCommandHandlerTests "
            + "against a FakeSdeAccessor instead",
        // ET-271: only ever writes a line a character's own gamelog file carries, which the shared harness has no
        // directory for; it publishes per run it added to, and HomefrontMoneyScenarioTests.S11 drives it end to end.
        [typeof(ImportRunBountyCommand)] = "needs a character's own gamelog file on disk to add anything, which the "
            + "shared harness has no directory for; proven end to end in HomefrontMoneyScenarioTests.S11",
        [typeof(ImportMissingGroupBountyCommand)] = "writes no run of its own: it only picks the runs and hands them to "
            + "ImportRunBountyCommand, whose own writes are the ones that signal"
    };

    /// <summary>Commands measured by ET-379 to publish no signal, each against the ticket that closes it. This list
    /// only ever shrinks: a new command gets a scenario or an exemption, never a place here.</summary>
    private static readonly IReadOnlyList<(string Ticket, Type[] Commands)> KnownGaps =
    [
        ("ET-381", [
            typeof(AddExternalMemberCommand), typeof(AssignMemberFitCommand), typeof(CoupleFleetToEsiCommand),
            typeof(CreateFleetInviteCommand), typeof(CreateSquadCommand), typeof(CreateWingCommand),
            typeof(DeleteSquadCommand), typeof(DeleteWingCommand), typeof(JoinFleetCommand), typeof(MoveMemberCommand),
            typeof(RemoveFleetMemberCommand), typeof(RenameSquadCommand), typeof(RenameWingCommand),
            typeof(ReportMemberFitVerdictCommand), typeof(ReportMemberInGameFleetCommand),
            typeof(RequestFleetSwitchCommand), typeof(RequestToJoinCommand), typeof(RespondToFleetInviteCommand),
            typeof(RespondToJoinRequestCommand), typeof(SetFleetCompositionCommand),
            typeof(SetFleetEsiAutomationCommand), typeof(SetFleetMemberAvailabilityCommand), typeof(SwapMembersCommand),
            typeof(SwitchToFleetCommand), typeof(TransferFleetOwnershipCommand), typeof(UncoupleFleetFromEsiCommand),
            typeof(AddFleetCompositionEntryCommand), typeof(AddFleetCompositionRoleCommand),
            typeof(CreateFleetCompositionCommand), typeof(DeleteFleetCompositionCommand),
            typeof(EditFleetCompositionCommand), typeof(EditFleetCompositionEntryCommand),
            typeof(EditFleetCompositionRoleCommand), typeof(RemoveFleetCompositionEntryCommand),
            typeof(RemoveFleetCompositionRoleCommand), typeof(ReorderFleetCompositionEntriesCommand),
            typeof(ReorderFleetCompositionRolesCommand)
        ]),
        ("ET-382", [
            typeof(CreateApiKeyCommand), typeof(DeleteApiKeyCommand), typeof(RevokeApiKeyCommand),
            typeof(SetApiKeyScopesCommand), typeof(ImportFitFromTextCommand), typeof(ImportFittingsFromEsiCommand),
            typeof(PushFittingToEsiCommand), typeof(ShareFittingCommand), typeof(RecordCombatCommand),
            typeof(LinkKillmailsToRunsCommand), typeof(SetKillmailRunLinkCommand), typeof(EnqueueMessageCommand),
            typeof(RespondToMessageCommand), typeof(DeleteSettingCommand), typeof(SetSettingCommand),
            typeof(AddShipCommand), typeof(AddSyncLogCommand)
        ])
    ];

    /// <summary>The size of <see cref="KnownGaps"/>. Closing a gap means taking it off the list and lowering this with
    /// it; raising it is the one change to this file a reviewer should refuse.</summary>
    private const int KnownGapCount = 54;

    /// <summary>For every command outside Runs that signals: real state to run it against, and what its signal must
    /// name.</summary>
    private static readonly IReadOnlyDictionary<Type, Arrange> Scenarios = new Dictionary<Type, Arrange>
    {
        [typeof(CreateFleetCommand)] = (dispatcher, cancellationToken) =>
        {
            long? fleetId = null;
            return Task.FromResult(new Act(async () =>
            {
                Result<long> created = await dispatcher.Send(_Create(), cancellationToken);
                fleetId = created.Value;
                return created;
            }, published => fleetId is { } id && _Names(id)(published)));
        },

        [typeof(EditFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new EditFleetCommand(fleetId, "Renamed", null, FleetVisibility.Public,
                null, null, default, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(StartFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new StartFleetCommand(fleetId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(StopFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _StartedAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new StopFleetCommand(fleetId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(ConcludeFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _StartedAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new ConcludeFleetCommand(fleetId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(DisbandFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new DisbandFleetCommand(fleetId, Owner), cancellationToken), _Names(fleetId));
        }
    };

    public static TheoryData<string> CommandsWithAScenario()
    {
        var names = new TheoryData<string>();
        foreach (Type command in Scenarios.Keys)
            names.Add(command.Name);
        return names;
    }

    [Fact]
    public void EveryCommand_HasAScenarioProvingItSignals_OrAReason_OrAKnownGap()
    {
        Type[] commands = _Commands();
        Assert.NotEmpty(commands);
        Type[] gaps = [.. KnownGaps.SelectMany(gap => gap.Commands)];
        Type[] proven = [.. Scenarios.Keys.Concat(RunsChangedSignalCoverageTests.ProvenCommands)];

        Type[] unaccounted = [.. commands.Where(command =>
            !proven.Contains(command) && !Exempt.ContainsKey(command) && !gaps.Contains(command))];
        Assert.True(unaccounted.Length == 0,
            "These commands have no scenario proving they publish their module's signal, and no reason on the exemption "
            + $"list why they need not: {string.Join(", ", unaccounted.Select(command => command.Name))}. Have the handler "
            + "publish the signal once its write is done and add a scenario to CommandSignalCoverageTests (or, for Runs, "
            + "RunsChangedSignalCoverageTests).");

        Type[] listed = [.. proven, .. Exempt.Keys, .. gaps];
        Assert.DoesNotContain(listed, command => !commands.Contains(command));
        Type[] listedTwice = [.. listed.GroupBy(command => command).Where(group => group.Count() > 1).Select(group => group.Key)];
        Assert.True(listedTwice.Length == 0,
            $"Listed more than once: {string.Join(", ", listedTwice.Select(command => command.Name))}. A command that "
            + "now has a scenario comes off the exemption or known-gap list.");
        Assert.All(Exempt, exemption => Assert.False(string.IsNullOrWhiteSpace(exemption.Value)));
    }

    [Fact]
    public void KnownGaps_OnlyShrink_AndEachCitesTheTicketThatClosesIt()
    {
        Assert.All(KnownGaps, gap => Assert.Matches(new Regex(@"^ET-\d+$"), gap.Ticket));
        Assert.True(KnownGaps.Sum(gap => gap.Commands.Length) == KnownGapCount,
            $"The known-gap list holds {KnownGaps.Sum(gap => gap.Commands.Length)} commands, not {KnownGapCount}. A gap "
            + "closed comes off the list and lowers KnownGapCount; a new command never goes on it.");
    }

    [Fact]
    public void EveryScenario_BelongsToAModuleWithASignal()
    {
        Assert.All(Scenarios.Keys.Concat(RunsChangedSignalCoverageTests.ProvenCommands),
            command => Assert.True(Signals.ContainsKey(_Module(command)), $"{command.Name} has no module signal mapped."));
    }

    [Theory]
    [MemberData(nameof(CommandsWithAScenario))]
    public async Task Command_PublishesItsModuleSignal_ForWhatItChanged(string commandName)
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Signal FC", Owner), cancellationToken);
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        KeyValuePair<Type, Arrange> scenario = Scenarios.Single(candidate => candidate.Key.Name == commandName);
        Type signal = Signals[_Module(scenario.Key)];
        Act act = await scenario.Value(dispatcher, cancellationToken);

        List<IIntegrationEvent> signalled = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<IIntegrationEvent>(published =>
            {
                if (signal.IsInstanceOfType(published))
                    signalled.Add(published);
            });
        Result outcome = await act.Send();

        Assert.True(outcome.IsSuccess, outcome.Messages.FirstOrDefault()?.Text);
        Assert.True(signalled.Count > 0, $"{commandName} changed state and published no {signal.Name}.");
        Assert.Contains(signalled, act.IsAbout);
    }

    private static Type[] _Commands() =>
    [
        .. typeof(ICommandHandler<>).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && type.Namespace?.StartsWith(ModulesNamespace, StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                               && (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                                   || contract.GetGenericTypeDefinition() == typeof(ICommandHandler<>)))
            .Select(contract => contract.GetGenericArguments()[0])
            .Distinct()
    ];

    private static string _Module(Type command)
    {
        string ns = command.Namespace ?? throw new InvalidOperationException($"{command.Name} has no namespace.");
        return ns[ModulesNamespace.Length..].Replace(".Commands", "", StringComparison.Ordinal);
    }

    private static Predicate<IIntegrationEvent> _Names(long fleetId) =>
        published => published is FleetChangedEvent changed && changed.FleetId == fleetId;

    private static CreateFleetCommand _Create() =>
        new("Signal fleet", null, FleetVisibility.Public, null, null, default, Owner);

    private static async Task<long> _CreateAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<long> created = await dispatcher.Send(_Create(), cancellationToken);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static async Task<long> _StartedAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        long fleetId = await _CreateAsync(dispatcher, cancellationToken);
        Assert.True((await dispatcher.Send(new StartFleetCommand(fleetId, Owner), cancellationToken)).IsSuccess);
        return fleetId;
    }

    /// <summary>Builds the state a command needs and hands back the command itself, not yet sent.</summary>
    private delegate Task<Act> Arrange(IDispatcher dispatcher, CancellationToken cancellationToken);

    /// <param name="IsAbout">Whether a published signal names what the command changed.</param>
    private sealed record Act(Func<Task<Result>> Send, Predicate<IIntegrationEvent> IsAbout);
}
