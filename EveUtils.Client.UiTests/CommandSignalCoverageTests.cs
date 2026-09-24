using System.Text.RegularExpressions;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Commands;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Queries;
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
    private const int Other = 95001681;
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
            + "ImportRunBountyCommand, whose own writes are the ones that signal",
        // ET-381: asking members to come over changes no fleet — no roster, no seat, no invite. It only enqueues a
        // message per member, which is the Messaging module's to signal (ET-382); the answer runs SwitchToFleetCommand.
        [typeof(RequestFleetSwitchCommand)] = "changes no fleet state: it only enqueues a message per member, which "
            + "Messaging signals; the member's answer runs SwitchToFleetCommand, which signals the move"
    };

    /// <summary>Commands measured by ET-379 to publish no signal, each against the ticket that closes it. This list
    /// only ever shrinks: a new command gets a scenario or an exemption, never a place here.</summary>
    private static readonly IReadOnlyList<(string Ticket, Type[] Commands)> KnownGaps =
    [

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
    private const int KnownGapCount = 17;

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
        },

        [typeof(CreateWingCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new CreateWingCommand(fleetId, "Wing 2", Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(RenameWingCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long wingId = await _WingAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new RenameWingCommand(wingId, "Logistics", Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(DeleteWingCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long wingId = await _WingAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new DeleteWingCommand(wingId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(CreateSquadCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long wingId = await _WingAsync(dispatcher, fleetId, cancellationToken);
            return new Act(async () => await dispatcher.Send(new CreateSquadCommand(wingId, "Squad 2", Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(RenameSquadCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long squadId = await _SquadAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new RenameSquadCommand(squadId, "Tackle", Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(DeleteSquadCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long squadId = await _SquadAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new DeleteSquadCommand(squadId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(JoinFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new JoinFleetCommand(fleetId, Other), cancellationToken), _Names(fleetId));
        },

        [typeof(AddExternalMemberCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new AddExternalMemberCommand(fleetId, Other, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(MoveMemberCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _JoinedAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new MoveMemberCommand(memberId, FleetRole.Unassigned, -1, -1, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(SwapMembersCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long otherId = await _JoinedAsync(dispatcher, fleetId, cancellationToken);
            long ownerId = await _MemberAsync(dispatcher, fleetId, Owner, cancellationToken);
            return new Act(() => dispatcher.Send(new SwapMembersCommand(ownerId, otherId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(RemoveFleetMemberCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _JoinedAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new RemoveFleetMemberCommand(memberId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(TransferFleetOwnershipCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            await _JoinedAsync(dispatcher, fleetId, cancellationToken);
            return new Act(() => dispatcher.Send(new TransferFleetOwnershipCommand(fleetId, Other, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(AssignMemberFitCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _MemberAsync(dispatcher, fleetId, Owner, cancellationToken);
            return new Act(() => dispatcher.Send(new AssignMemberFitCommand(memberId, _Fit(), null, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(ReportMemberFitVerdictCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _MemberAsync(dispatcher, fleetId, Owner, cancellationToken);
            Assert.True((await dispatcher.Send(new AssignMemberFitCommand(memberId, _Fit(), null, Owner), cancellationToken)).IsSuccess);
            return new Act(async () => await dispatcher.Send(
                new ReportMemberFitVerdictCommand(memberId, FitSkillVerdict.CanFly, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(ReportMemberInGameFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _MemberAsync(dispatcher, fleetId, Owner, cancellationToken);
            return new Act(async () => await dispatcher.Send(new ReportMemberInGameFleetCommand(memberId, true, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(SetFleetMemberAvailabilityCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long memberId = await _MemberAsync(dispatcher, fleetId, Owner, cancellationToken);
            return new Act(() => dispatcher.Send(new SetFleetMemberAvailabilityCommand(
                memberId, FleetMemberAvailability.SignedOff, "Out tonight", Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(SetFleetCompositionCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            long compositionId = await _CompositionAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new SetFleetCompositionCommand(fleetId, compositionId, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(CoupleFleetToEsiCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new CoupleFleetToEsiCommand(fleetId, 1234567, Owner, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(UncoupleFleetFromEsiCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            Assert.True((await dispatcher.Send(new CoupleFleetToEsiCommand(fleetId, 1234567, Owner, Owner), cancellationToken)).IsSuccess);
            return new Act(() => dispatcher.Send(new UncoupleFleetFromEsiCommand(fleetId, Owner), cancellationToken), _Names(fleetId));
        },

        [typeof(SetFleetEsiAutomationCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new SetFleetEsiAutomationCommand(fleetId, Owner, true, true), cancellationToken),
                _Names(fleetId));
        },

        [typeof(CreateFleetInviteCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(_Invite(fleetId), cancellationToken), _Names(fleetId));
        },

        [typeof(RespondToFleetInviteCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken);
            Result<FleetInvitePayload> invited = await dispatcher.Send(_Invite(fleetId), cancellationToken);
            Assert.True(invited.IsSuccess);
            long inviteId = invited.Value?.InviteId ?? throw new InvalidOperationException("The invite carried no id.");
            return new Act(async () => await dispatcher.Send(new RespondToFleetInviteCommand(inviteId, true, Other), cancellationToken),
                _Names(fleetId));
        },

        [typeof(RequestToJoinCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken, FleetVisibility.InviteOnly);
            return new Act(async () => await dispatcher.Send(new RequestToJoinCommand(fleetId, Other), cancellationToken), _Names(fleetId));
        },

        [typeof(RespondToJoinRequestCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _CreateAsync(dispatcher, cancellationToken, FleetVisibility.InviteOnly);
            Result<FleetJoinRequestPayload> requested = await dispatcher.Send(new RequestToJoinCommand(fleetId, Other), cancellationToken);
            Assert.True(requested.IsSuccess);
            long requestId = requested.Value?.RequestId ?? throw new InvalidOperationException("The request carried no id.");
            return new Act(() => dispatcher.Send(new RespondToJoinRequestCommand(requestId, true, Owner), cancellationToken),
                _Names(fleetId));
        },

        [typeof(SwitchToFleetCommand)] = async (dispatcher, cancellationToken) =>
        {
            long fleetId = await _StartedAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(new SwitchToFleetCommand(fleetId, Other), cancellationToken), _Names(fleetId));
        },

        [typeof(CreateFleetCompositionCommand)] = (dispatcher, cancellationToken) =>
        {
            long? compositionId = null;
            return Task.FromResult(new Act(async () =>
            {
                Result<long> created = await dispatcher.Send(_Composition(), cancellationToken);
                compositionId = created.Value;
                return created;
            }, published => compositionId is { } id && _NamesComposition(id)(published)));
        },

        [typeof(EditFleetCompositionCommand)] = async (dispatcher, cancellationToken) =>
        {
            long compositionId = await _CompositionAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new EditFleetCompositionCommand(compositionId, "Armor", null, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(DeleteFleetCompositionCommand)] = async (dispatcher, cancellationToken) =>
        {
            long compositionId = await _CompositionAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new DeleteFleetCompositionCommand(compositionId, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(AddFleetCompositionRoleCommand)] = async (dispatcher, cancellationToken) =>
        {
            long compositionId = await _CompositionAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(
                new AddFleetCompositionRoleCommand(compositionId, "Logistics", null, Owner), cancellationToken), _NamesComposition(compositionId));
        },

        [typeof(EditFleetCompositionRoleCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, long roleId) = await _RoleAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new EditFleetCompositionRoleCommand(roleId, "Tackle", 2, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(RemoveFleetCompositionRoleCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, long roleId) = await _RoleAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new RemoveFleetCompositionRoleCommand(roleId, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(ReorderFleetCompositionRolesCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, long roleId) = await _RoleAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new ReorderFleetCompositionRolesCommand(compositionId, [roleId], Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(AddFleetCompositionEntryCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, long roleId) = await _RoleAsync(dispatcher, cancellationToken);
            return new Act(async () => await dispatcher.Send(
                new AddFleetCompositionEntryCommand(roleId, _Fit(), null, Owner), cancellationToken), _NamesComposition(compositionId));
        },

        [typeof(EditFleetCompositionEntryCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, _, long entryId) = await _EntryAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new EditFleetCompositionEntryCommand(entryId, 3, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(RemoveFleetCompositionEntryCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, _, long entryId) = await _EntryAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new RemoveFleetCompositionEntryCommand(entryId, Owner), cancellationToken),
                _NamesComposition(compositionId));
        },

        [typeof(ReorderFleetCompositionEntriesCommand)] = async (dispatcher, cancellationToken) =>
        {
            (long compositionId, long roleId, long entryId) = await _EntryAsync(dispatcher, cancellationToken);
            return new Act(() => dispatcher.Send(new ReorderFleetCompositionEntriesCommand(roleId, [entryId], Owner), cancellationToken),
                _NamesComposition(compositionId));
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

    private static Predicate<IIntegrationEvent> _NamesComposition(long compositionId) =>
        published => published is CompositionChangedEvent changed && changed.Data.CompositionId == compositionId;

    private static CreateFleetCommand _Create(FleetVisibility visibility = FleetVisibility.Public) =>
        new("Signal fleet", null, visibility, null, null, default, Owner);

    private static async Task<long> _CreateAsync(
        IDispatcher dispatcher, CancellationToken cancellationToken, FleetVisibility visibility = FleetVisibility.Public)
    {
        Result<long> created = await dispatcher.Send(_Create(visibility), cancellationToken);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static async Task<long> _MemberAsync(IDispatcher dispatcher, long fleetId, int characterId, CancellationToken cancellationToken) =>
        (await dispatcher.Query(new ListMembersQuery(fleetId), cancellationToken)).Single(member => member.CharacterId == characterId).Id;

    private static async Task<long> _JoinedAsync(IDispatcher dispatcher, long fleetId, CancellationToken cancellationToken)
    {
        Assert.True((await dispatcher.Send(new JoinFleetCommand(fleetId, Other), cancellationToken)).IsSuccess);
        return await _MemberAsync(dispatcher, fleetId, Other, cancellationToken);
    }

    private static async Task<long> _WingAsync(IDispatcher dispatcher, long fleetId, CancellationToken cancellationToken) =>
        (await dispatcher.Query(new ListWingsQuery(fleetId), cancellationToken)).First().Id;

    private static async Task<long> _SquadAsync(IDispatcher dispatcher, long fleetId, CancellationToken cancellationToken)
    {
        long wingId = await _WingAsync(dispatcher, fleetId, cancellationToken);
        return (await dispatcher.Query(new ListSquadsQuery(wingId), cancellationToken)).First().Id;
    }

    private static CreateFleetInviteCommand _Invite(long fleetId) =>
        new(fleetId, Other, FleetRole.SquadMember, null, null, null, Owner);

    private static FitReference _Fit() => new() { ShipTypeId = 11987, FitName = "Guardian", ContentHash = "signal-guardian" };

    private static CreateFleetCompositionCommand _Composition() => new("Signal doctrine", null, true, Owner);

    private static async Task<long> _CompositionAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<long> created = await dispatcher.Send(_Composition(), cancellationToken);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static async Task<(long CompositionId, long RoleId)> _RoleAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        long compositionId = await _CompositionAsync(dispatcher, cancellationToken);
        Result<long> role = await dispatcher.Send(new AddFleetCompositionRoleCommand(compositionId, "Logistics", null, Owner), cancellationToken);
        Assert.True(role.IsSuccess);
        return (compositionId, role.Value);
    }

    private static async Task<(long CompositionId, long RoleId, long EntryId)> _EntryAsync(
        IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        (long compositionId, long roleId) = await _RoleAsync(dispatcher, cancellationToken);
        Result<long> entry = await dispatcher.Send(new AddFleetCompositionEntryCommand(roleId, _Fit(), null, Owner), cancellationToken);
        Assert.True(entry.IsSuccess);
        return (compositionId, roleId, entry.Value);
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
