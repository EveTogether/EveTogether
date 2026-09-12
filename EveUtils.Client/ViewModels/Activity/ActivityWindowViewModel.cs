using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Control;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Grouping;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The activity window (ET-98): a run you are still flying, rather than a form you fill in once it is over. That is
/// the whole difference from every other tracker, and it is why the clock is the largest thing on it.
///
/// START creates the stored <c>Run</c> and the window follows that row from there — the clock, the loot, the enemies
/// and the bounties all hang off one id, so "a run is running" means the same thing here as it does in the database.
/// STOP only stops the clock: the row stays open until SAVE or DISCARD, so loot copied after the last rat still
/// lands on the run it came from.
///
/// What stands under the clock is not this class's: the run's type names its sections (<see cref="RunTypeCatalogue"/>),
/// each section is a module of its own (<see cref="RunSectionModules"/>), and a module sees this window only as
/// <see cref="IRunWindowContext"/> (ET-236). What is left here is the frame — the header, the clock, the run controls
/// and the run's own lifecycle — and the state those and the sections share.
/// </summary>
public sealed partial class ActivityWindowViewModel : ObservableObject, IDisposable, IRunWindowContext
{
    /// <summary>Once a second. The readout is a clock, and a clock cannot be read faster than it ticks.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnstartedFleetNoticeRefreshInterval = TimeSpan.FromSeconds(30);

    // Amber then red, on the last five and the last two minutes. Both are enough time to leave, which is the only
    // decision the clock exists to inform.
    private static readonly TimeSpan WarningAt = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CriticalAt = TimeSpan.FromMinutes(2);

    private const string NoClock = "--:--";

    private readonly IServiceProvider _services;
    private readonly GamelogClientService? _gamelog;
    private readonly IDisposable? _metricSubscription;
    private readonly IDisposable? _lootSubscription;
    private readonly IDisposable? _fleetRunStartedSubscription;
    private readonly IDisposable? _fleetRunStoppedSubscription;
    private readonly IDisposable? _fleetRunDiscardedSubscription;
    private readonly IDisposable? _fleetRunAbyssalUpdatedSubscription;
    private readonly IDisposable? _fleetRunPreparedSubscription;
    private readonly IDisposable? _fleetPilotStoppedSubscription;
    private readonly IDisposable? _fleetPilotResumedSubscription;

    // Every pilot's own leg of a run whose clock is per pilot, as each announced it (ET-243) — what the fleet's clock is
    // made of. App-wide, so this window knows who went in before it opened.
    private readonly FleetRunLegs? _fleetLegs;

    // Own characters riding alongside the acting one (ET-210) in a run whose clock is per pilot, picked but not yet
    // seen inside the pocket on their own account — a hauler who stays outside never leaves this list (ET-250).
    private readonly List<(int Id, string Name)> _ownLegsPending = [];

    // Whether each own character already on their own leg — the acting one aside, which _RefreshLocation already
    // tracks through InsideAbyssal — was last seen inside the pocket, keyed by character id. Read by
    // _RefreshOwnPilotLegs to tell a fresh crossing from one already accounted for.
    private readonly Dictionary<int, bool> _ownLegWasInside = [];

    // This window put its run out to the fleet before anybody was in it (ET-246), so closing it unanswered calls it off.
    private bool _hasPreparedOffer;

    // What the location watch could see on the last tick: whether this pilot's own way into the pocket would be seen.
    private bool _canSeeCrossing;
    private bool _startedOnEntry;

    // The fleet's latest location sample per member, so the envelope is re-taken over the whole fleet on every
    // sample rather than over whichever one happened to arrive last.
    private readonly Dictionary<int, MetricSample> _fleetLocations = [];

    // What each member last said their run had made. Separate from the locations because loot, bounty and location
    // are three separate opt-ins: the common member shares one of them and not the others.
    private readonly Dictionary<int, (decimal? Loot, decimal? Bounty)> _fleetIsk = [];

    private DispatcherTimer? _timer;
    private bool _isManualRun;
    // The discard this window ordered comes back to it on the bus. Without this the commander's own window would
    // take the member's treatment — a notice and a Discarded state — on its way out.
    private bool _isDiscarding;
    private int? _runCharacterId;
    private int? _namedCharacterId;
    private DateTime? _unstartedFleetNoticeCheckedAtUtc;
    private PendingCopy? _pendingCopy;
    private string? _runCharacterName;
    private int? _commanderNameId;
    private string? _commanderName;

    /// <summary>Every section this window has built, kept for as long as the window lives rather than as long as its
    /// type claims it — a type change mid-run hides a section, it does not throw its state away.</summary>
    private readonly Dictionary<RunSectionId, RunWindowSection> _sections = [];

    public ActivityWindowViewModel(ActivityKind kind, IServiceProvider services)
    {
        Kind = kind;
        _services = services;
        _gamelog = services.GetService<GamelogClientService>();
        if (_gamelog is not null)
            _gamelog.BountyObserved += _OnBountyObserved;

        _metricSubscription = services.GetService<IEventBus>()?.Subscribe<FleetMetricEvent>(_OnFleetMetric);
        // The clipboard records loot; this window shows it, and the two never met. Without this the LOOT section
        // only ever held what was already stored when the window loaded or started its run.
        _lootSubscription = services.GetService<IEventBus>()?.Subscribe<RunLootCapturedEvent>(_OnRunLootCaptured);
        // What the commander does to the shared run, as it happens. Until these existed the announcements crossed to
        // this machine and no window was listening for any of them, so a member saw the run start, stop and end
        // without a single thing changing in front of him (Raymond, 2026-09-03).
        _fleetRunStartedSubscription = services.GetService<IEventBus>()?.Subscribe<FleetRunGroupCodeEvent>(_OnFleetRunStarted);
        _fleetRunStoppedSubscription = services.GetService<IEventBus>()?.Subscribe<FleetRunStoppedEvent>(_OnFleetRunStopped);
        _fleetRunDiscardedSubscription = services.GetService<IEventBus>()?.Subscribe<FleetRunDiscardedEvent>(_OnFleetRunDiscarded);
        // The commander changed the pocket's tier or weather after this member already joined (ET-241) — same
        // shape as the three subscriptions above, kept in step for as long as the run runs.
        _fleetRunAbyssalUpdatedSubscription = services.GetService<IEventBus>()?
            .Subscribe<FleetRunGroupAbyssalUpdatedEvent>(_OnFleetAbyssalUpdated);
        // A pocket's own lifecycle across the fleet (ET-246, ET-243): the commander's run set up before anyone is in,
        // and every pilot's own way out.
        _fleetRunPreparedSubscription = services.GetService<IEventBus>()?
            .Subscribe<FleetRunGroupPreparedEvent>(_OnFleetRunPrepared);
        _fleetPilotStoppedSubscription = services.GetService<IEventBus>()?
            .Subscribe<FleetRunPilotStoppedEvent>(_OnFleetPilotStopped);
        _fleetPilotResumedSubscription = services.GetService<IEventBus>()?
            .Subscribe<FleetRunPilotResumedEvent>(_OnFleetPilotResumed);
        _fleetLegs = services.GetService<FleetRunLegs>();
        RunLoot = services.GetService<CqrsDispatcher>() is { } dispatcher
            ? new RunLootViewModel(dispatcher, services.GetService<IAppraisalProvider>(), services.GetService<ISdeAccessor>())
            : null;
        if (RunLoot is not null)
            RunLoot.PropertyChanged += (_, _) => _RefreshSummaries();
        LootOverview = services.GetService<CqrsDispatcher>() is { } overviewDispatcher
            ? new ActivityLootViewModel(() => new RunLootViewModel(overviewDispatcher,
                    services.GetService<IAppraisalProvider>(), services.GetService<ISdeAccessor>(),
                    services.GetService<ITypeImageProvider>()),
                services.GetService<ICharacterPortraitProvider>())
            : null;
        if (LootOverview is not null)
            LootOverview.PropertyChanged += (_, _) => _RefreshSummaries();

        _SyncSectionsToType();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Which kind of run this window is showing — the same value the store files it under, given to the
    /// window rather than worked out here (ET-174 AC-3). Fixed for the life of the window: a run does not turn into
    /// another kind halfway through.</summary>
    public ActivityKind Kind { get; }

    /// <summary>What this run is, from the one catalogue every run screen reads (ET-226). Unlike <see cref="Kind"/> it
    /// can change mid-run: a site started without a scanner group gets one when the run it adopts carries it.</summary>
    public RunTypeDefinition RunType => RunTypeCatalogue.For(Kind, SignatureGroup);

    /// <summary>The sections the run's type has, in the order <see cref="RunSectionModules"/> gives them — what the
    /// window draws under its clock.</summary>
    public ObservableCollection<RunWindowSection> Sections { get; } = [];

    IServiceProvider IRunWindowContext.Services => _services;

    int? IRunWindowContext.RunCharacterId => _runCharacterId;

    int? IRunWindowContext.ActingCharacterId => _ActingCharacterId();

    bool IRunWindowContext.IsFleetCommander => Authority.IsFleetCommander;

    /// <summary>The run on screen, for what belongs to one run only: the registration way, the two paste boxes and
    /// the starting-hold picker. Follows the character column.</summary>
    public RunLootViewModel? RunLoot { get; }

    /// <summary>The LOOT list itself, one block per run in the group (ET-215) — the same component the saved
    /// activity's detail screen shows. Each participant's block is their own run's loot, whichever character the
    /// column happens to show, which is also what the live TOTAL ISK sums for a group (ET-211).</summary>
    public ActivityLootViewModel? LootOverview { get; }

    /// <summary>Only for what is genuinely the abyss's own: the 20-minute deadline, the tier and weather, the
    /// per-member anchors. Never for "and everything else is a site" — that is what this window used to do.</summary>
    private bool _IsInPocket => RunType.Space is RunSpace.AbyssalPocket;

    // ── The run ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>This run's START — the stored run's own <c>StartedAtUtc</c>, or the commander's for a joined site. In a
    /// pocket it is this pilot's own way in and nobody else's (ET-246); the fleet's earliest is the FLEET line.</summary>
    [ObservableProperty] private DateTime? _anchorUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(IsStartButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsStopButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsKeepRunButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsDiscardButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsSaveButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsTimeCorrectionShown))]
    [NotifyPropertyChangedFor(nameof(RunOriginText))]
    private ActivityRunState _runState;

    // ── Who may steer the shared run ────────────────────────────────────────────────────────────────
    // Re-tested on every change rather than captured at start: an FC handover mid-run moves the buttons with it
    // (ET-105). RunControlAuthority is the only place that decides; everything here just binds to it.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsStopButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsDiscardButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsCommandStatusShown))]
    [NotifyPropertyChangedFor(nameof(CommandStatusText))]
    // Unknown, not the four nulls this was built from: those land in From's solo branch, so a window that knows
    // nothing yet came up with every button on and only became right once a read had landed (ET-150).
    private RunControlAuthority _authority = new(RunControlAuthorityLevel.Unknown, null);

    /// <summary>The run row this window is writing to, once one has been started. Null for a window that is only
    /// showing the frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveButtonVisible))]
    private Guid? _runId;

    /// <summary>The LOOT section reads the run this window is on, so the id travels here rather than at each of the
    /// six places that refresh it — one of which would have been forgotten.</summary>
    partial void OnRunIdChanged(Guid? value)
    {
        if (RunLoot is not null)
            RunLoot.RunId = value;
        // A group's blocks follow its participants, not the column; only a run the store has not named any
        // participants for yet takes its block from here.
        if (Participants.Count == 0)
            _SyncLootOverview();
    }

    /// <summary>The fleet this run belongs to, or null when the window was never told of one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFleetShown))]
    [NotifyPropertyChangedFor(nameof(HasFleetNotice))]
    private long? _fleetId;

    /// <summary>
    /// How many fleets this window's pilot is in, as the last sweep counted them. More than one is the state
    /// <see cref="_ActingFleetId"/> answers with null — it cannot pick a fleet for the player — and a run with no
    /// fleet id is filed under nobody and shared with nobody.
    ///
    /// Held so the window can say that out loud. It used to happen in silence: two fleets was enough to turn a run
    /// solo with no toast, no warning and no gap on screen to notice (ET-165). The same rule ET-65 AC-7 set for the
    /// run controls — an empty state is a state, not silence.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleetNotice))]
    [NotifyPropertyChangedFor(nameof(FleetNoticeText))]
    private int _fleetsInPlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleetNotice))]
    [NotifyPropertyChangedFor(nameof(FleetNoticeText))]
    private string? _unstartedFleetName;

    /// <summary>
    /// How many forming fleets <see cref="_UnstartedFleetNameAsync"/> found when it was more than one. Set instead of
    /// <see cref="UnstartedFleetName"/>, never beside it: naming one of several would be a guess dressed up as an
    /// answer — the mistake ET-201 exists to undo (PR #223, withdrawn) — so the notice states the count and nobody's
    /// name.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleetNotice))]
    [NotifyPropertyChangedFor(nameof(FleetNoticeText))]
    private int _formingFleetCount;

    [ObservableProperty] private string? _groupCode;

    // The group code is what makes a run shared, so the verdict has to be redone when it arrives. It arrives after
    // the constructor: FleetRunWindowPresenter sets it through an object initializer, which runs once the window has
    // already worked out an authority for a run it then still read as its own.
    partial void OnGroupCodeChanged(string? value) => _ = RefreshFleetCommandAsync(DateTime.UtcNow);

    [ObservableProperty] private DateTime? _stoppedAtUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(FleetStatusText))]
    private int _fleetMemberCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(FleetStatusText))]
    private int _anchoredFleetMemberCount;

    /// <summary>The solar system the run is in. Always null in the abyss — a pocket has no location, and the window
    /// says so rather than leaving the field blank.</summary>
    [ObservableProperty] private string? _solarSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsideAbyssal))]
    private bool? _insideAbyssal;

    [ObservableProperty] private string? _locationDisplay;

    [ObservableProperty] private long _bountyIsk;

    /// <summary>What the copied signature's own text said it was (ET-100) — the raw scan-window field, not
    /// anything the SDE could enrich it to. Always null in the abyss; a filament carries no signature. The one fact
    /// the run's type can change on mid-run, so everything the type decides is announced with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunType))]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(IsMissionLevelShown))]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    private string? _signatureGroup;

    partial void OnSignatureGroupChanged(string? value) => _SyncSectionsToType();

    /// <summary>The scan's own id, e.g. <c>RUS-326</c> — what tells one Sansha Refuge from the next one, which a
    /// site name cannot. Shown after the location, so the row names the site as well as the system.</summary>
    [ObservableProperty] private string? _signatureId;

    /// <summary>The signature's name once fully scanned — same field, same source as <see cref="SignatureGroup"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    private string? _signatureName;

    /// <summary>What the site catalogue carries under <see cref="SignatureName"/> (ET-80). Empty is the ordinary
    /// case rather than a fault — the match is on the English name only, so a miss cannot prove the site is absent —
    /// and so is more than one, since 218 catalogue names are shared by 613 dungeons. Nothing below ever picks one.</summary>
    [ObservableProperty] private IReadOnlyList<SdeSite> _matchedSites = [];

    // Whether this window starts its run by itself once it has settled, instead of waiting for START. Set by the
    // clipboard signature offer (ET-158), which has no button to press.
    public bool StartsOnArrival { get; set; }

    // The mission's own facts, set by ClipboardMissionOffer (ET-172 sub 4) before the window is shown — null on
    // every kind but Mission, same as Run.AgentId/MissionLevel themselves. The system comes from the agent's own
    // station, never from the clipboard text (ET-172 sub 4 AC-4) — there is no location text to parse anyway.
    public int? MissionAgentId { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissionLevelShown))]
    [NotifyPropertyChangedFor(nameof(MissionLevelText))]
    private int? _missionLevel;
    public int? MissionSolarSystemId { get; set; }

    public bool IsMissionLevelShown => RunType.HasAgent && MissionLevel is not null;

    public string MissionLevelText => MissionLevel is { } level ? $"Level {level}" : string.Empty;

    // The reward lines a mission's clipboard capture already carried at accept time, written onto the run the
    // moment it starts rather than waited for — a mission is not looted the way a site is (ET-174 AC-4).
    public IReadOnlyList<RunParameterInput> PendingParameters { get; set; } = [];

    // ── The sections ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bring <see cref="Sections"/> to what the run's type claims. A section is built the first time a type
    /// claims it and kept from then on; one the type no longer claims leaves the screen with its state intact, and the
    /// ones that stay keep their instance — open or folded as the pilot left them.</summary>
    private void _SyncSectionsToType()
    {
        List<RunWindowSection> claimed = [];
        foreach (RunSectionModule module in RunSectionModules.All)
        {
            if (module.CreateForWindow is not { } create || !RunType.WindowSections.Contains(module.Id))
                continue;

            if (!_sections.TryGetValue(module.Id, out RunWindowSection? section))
                _sections[module.Id] = section = create(this);
            claimed.Add(section);
        }

        Sections.ReconcileTo(claimed);
    }

    /// <summary>Every section this window built, claimed or not, in screen order — what the lifecycle reaches.</summary>
    private IEnumerable<RunWindowSection> _AllSections() =>
        RunSectionModules.All.Select(module => _sections.GetValueOrDefault(module.Id)).OfType<RunWindowSection>();

    /// <summary>
    /// Whose run this is, by name, for the header. The window knew this all along and never said it: the FLEET
    /// section named everyone you fly beside without ever naming you (Raymond, 2026-09-02). Null until a character
    /// is settled, which the header then says rather than leaving blank.
    ///
    /// The same character <see cref="_ActingCharacterId"/> answers with — one idea of who you are, shown and used.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActingCharacter))]
    [NotifyPropertyChangedFor(nameof(ActingCharacterText))]
    private string? _actingCharacterName;

    public bool HasActingCharacter => ActingCharacterName is not null;

    /// <summary>One name, or the group's — as many toons of this pilot can be filed under one run since ET-210, and
    /// the chip that used to promise "the character this run is filed under" was lying the moment a second row
    /// joined it (ET-130 deel 3).</summary>
    public string ActingCharacterText => Participants.Count > 1
        ? string.Join(" · ", Participants.Select(participant => participant.CharacterName))
        : ActingCharacterName ?? "no character yet";

    /// <summary>The header chip's click: before START, reopen the same "whose run is this" question the window
    /// would otherwise only ask once — the multi-select answer (ET-130 deel 3, ET-210) replaces whatever was picked
    /// before rather than adding to it, since nothing is running yet to add a character TO. Once a run is on the
    /// clock, clicking it instead offers to bring another of this pilot's flying characters into the group.</summary>
    [RelayCommand]
    private async Task PickCharacterAsync()
    {
        if (RunId is not null)
        {
            await _AddCharacterToRunningGroupAsync();
            return;
        }

        _runCharacterId = null;
        _runCharacterName = null;
        _namedCharacterId = null;
        await _ResolveCharacterAsync(mayAsk: true);
        await _RefreshActingCharacterAsync();
        _RefreshRunCharacters();
    }

    /// <summary>Add a flying character nobody has picked yet to this window's already-running group (ET-210), under
    /// the same GroupCode this run already has or, for a run that was solo until now, one minted for the occasion.
    /// </summary>
    private async Task _AddCharacterToRunningGroupAsync()
    {
        if (_services.GetService<ICharacterRegistry>() is not { } registry
            || _services.GetService<IDialogService>() is not { } dialogs
            || _services.GetService<CqrsDispatcher>() is null)
            return;

        List<Character> known = (await registry.GetAllAsync())
            .Where(character => character.EsiCharacterId is not null
                                 && Participants.All(participant => participant.CharacterId != character.EsiCharacterId))
            .ToList();
        List<Character> candidates = InGameCharacters.Among(known, _services.GetService<ILocalCharacterPresence>());
        if (candidates.Count == 0)
            return;

        IReadOnlyList<int>? picked = await dialogs.PickCharactersAsync("Add which character(s) to this run?",
            [.. candidates.Select(character => new CharacterPickOption(
                character.EsiCharacterId!.Value, character.Name, "EVE client running", Enabled: true))]);
        if (picked is not { Count: > 0 } || RunId is not { } runId)
            return;

        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        if (GroupCode is null)
        {
            // This run was solo until now, so nothing ties it to the new characters yet — mint a code and relink
            // this window's OWN row to it first, or the row picking these characters up would drop out of its own
            // group: GetRunGroupParticipantsQuery matches on GroupCode, not on "started this".
            GroupCode = RunGroupCode.Create();
            await dispatcher.Send(new LinkRunToGroupCodeCommand(runId, GroupCode, FleetId));
        }

        int? solarSystemId = _ResolveSolarSystemId();
        IEnumerable<Character> chosen =
            candidates.Where(candidate => picked.Contains(candidate.EsiCharacterId!.Value));
        // Same per-pilot rule as the initial pick (ET-250): a toon added mid-run to a pocket still crosses on its
        // own moment, not the instant it was added on this window.
        if (RunType.ClockPerPilot)
            _ownLegsPending.AddRange(chosen.Select(character => (character.EsiCharacterId!.Value, character.Name)));
        else
            foreach (Character character in chosen)
                await _SendAdditionalStartRunCommandAsync(
                    dispatcher, character.EsiCharacterId!.Value, character.Name, AnchorUtc ?? DateTime.UtcNow, solarSystemId);

        await _RefreshParticipantsAsync();
    }

    // ── Weather and tier ────────────────────────────────────────────────────────────────────────────
    // The pocket's own two facts, held here because the header shows them; the ACTIVITY section asks for them and
    // remembers them.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weather))]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    private int? _weatherIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(TierText))]
    private int? _tierIndex;

    public AbyssalWeather? Weather => WeatherIndex is { } index ? AbyssalWeather.All[index] : null;

    // The type guard states the invariant: the remembered indexes are restored for every window, and only a pocket
    // has either.
    public bool HasWeatherAndTier => _IsInPocket && WeatherIndex is not null && TierIndex is not null;

    /// <summary>Drives the one chip in the header that asks for something. Only ever true for an abyssal run — a
    /// site has neither.</summary>
    public bool NeedsWeatherAndTier => _IsInPocket && !HasWeatherAndTier;

    public string TierText => TierIndex is { } tier
        ? $"{AbyssalTiers.Names[tier]} (Tier {tier})"
        : "not set";

    // ── The clock ───────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _clockLabel = string.Empty;

    [ObservableProperty] private string _clockText = NoClock;

    [ObservableProperty] private bool _isClockWarning;

    [ObservableProperty] private bool _isClockCritical;

    [ObservableProperty] private string _startText = string.Empty;

    [ObservableProperty] private string _endText = string.Empty;

    /// <summary>The running total beside the clock (ET-210 review: "the same way TOTAL ISK already stands next to
    /// DURATION on the saved screen, only this one during rather than after"). Covers the WHOLE group, not whichever
    /// character the column happens to show — Jithran's stated preference, and the only reading that cannot flip the
    /// figure by switching a column, which is exactly the confusion round 3 of this same review cleaned up for
    /// bounty and enemies. See <see cref="_RefreshGroupTotalIsk"/> for what is and is not summed into it.</summary>
    [ObservableProperty] private bool _hasGroupTotalIsk;

    [ObservableProperty] private string _groupTotalIskText = string.Empty;

    // ── Armed, and the fleet's own clock ────────────────────────────────────────────────────────────
    // A pocket's run starts by itself on the way in, and until ET-246 nothing on the window said so. And a fleet's run
    // lasts from its first pilot in to its last one out (ET-243), which no single pilot's clock shows.

    /// <summary>Waiting for this pilot's way into the pocket — the window says so for as long as it waits.</summary>
    [ObservableProperty] private bool _isArmedShown;

    /// <summary>The way in will actually be seen. False is the window saying it cannot, and what to do instead.</summary>
    [ObservableProperty] private bool _isArmed;

    [ObservableProperty] private string _armedText = string.Empty;

    [ObservableProperty] private bool _hasFleetClock;

    [ObservableProperty] private string _fleetClockText = string.Empty;

    /// <summary>This pilot is out and somebody is still in: the activity is not over, only this pilot's part of it.</summary>
    [ObservableProperty] private bool _isWaitingForFleet;

    // ── Correcting the clock after the fact ─────────────────────────────────────────────────────────
    // Manual start and stop are the only source a site run has — there is no site-entry or site-exit line in the
    // gamelog to fall back on — so the human slack is part of the measurement: you press START once the fight is
    // already going, and STOP once the loot is already in the hold. Without a correction every stored duration is
    // systematically off, which is why this is part of the run and not a convenience.

    /// <summary>The start as the pilot corrected it, or null while the measured one still stands. Held beside
    /// <see cref="AnchorUtc"/> rather than over it: overwriting the measured moment would lose the difference the
    /// window exists to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeCorrected))]
    [NotifyPropertyChangedFor(nameof(TimeSourceText))]
    private DateTime? _correctedStartUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeCorrected))]
    [NotifyPropertyChangedFor(nameof(TimeSourceText))]
    private DateTime? _correctedStopUtc;

    [ObservableProperty] private string _startCorrectionText = string.Empty;

    [ObservableProperty] private string _endCorrectionText = string.Empty;

    /// <summary>Why a correction was refused, or null when there is nothing to refuse. A rejected time is never
    /// quietly straightened out: the pilot typed something, and what was wrong with it is the answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeCorrectionError))]
    private string? _timeCorrectionError;

    public bool HasTimeCorrectionError => TimeCorrectionError is not null;

    /// <summary>Only on a stopped run: while it is still going the clock is a measurement, and after SAVE the row
    /// is committed.</summary>
    public bool IsTimeCorrectionShown => RunState == ActivityRunState.Stopped;

    public bool IsTimeCorrected => CorrectedStartUtc is not null || CorrectedStopUtc is not null;

    /// <summary>Shown beside the figures, because this project says everywhere else whether a number was measured
    /// or typed and the clock is no exception.</summary>
    public string TimeSourceText => IsTimeCorrected
        ? "corrected by hand — this is what SAVE stores, and it moves this run only"
        : "measured from START and STOP";

    /// <summary>The title bar, from the run's type — "RUN" for a kind this build has never heard of, so a run stored
    /// by a later version still opens and still reads as a run (<see cref="RunTypeCatalogue.NewerBuildKind"/>).</summary>
    public string HeaderTitle => RunType.WindowTitle;

    // Start, stop and discard steer the run for everybody in it, so all three hang off the same authority (AC-4).
    // Start and stop are the same slot seen from two sides and never both apply: a run that is going can only be
    // stopped, and offering to re-start it over itself is what put START next to a ticking clock.
    /// <summary>Hidden while a copy is waiting: the answers there are SAVE, DISCARD and KEEP, and START would pick
    /// the run being waited on back up without answering any of them.</summary>
    public bool IsStartButtonVisible =>
        _MayTimeOwnLeg && RunState != ActivityRunState.Running && _pendingCopy is null;

    public bool IsStopButtonVisible => _MayTimeOwnLeg && RunState == ActivityRunState.Running;

    /// <summary>In a run whose clock is per pilot, START and STOP time this pilot's own leg and nobody else's (ET-246),
    /// so there they are every pilot's own buttons; DISCARD still ends the run for everybody and stays the commander's.
    /// </summary>
    private bool _MayTimeOwnLeg => Authority.CanControl
        || RunType.ClockPerPilot && RunState is not (ActivityRunState.Discarded or ActivityRunState.Saved);

    /// <summary>The third way out of a copy waiting behind a run (Raymond, 2026-09-04): SAVE and DISCARD both end
    /// the run and let the copy take over, and this one throws the copy away instead. It takes START's slot, which
    /// is empty for exactly as long as something is waiting.</summary>
    public bool IsKeepRunButtonVisible => Authority.CanControl && _pendingCopy is not null;

    /// <summary>Drop the waiting copy and carry on. The clock was stopped to hold that copy, not by the pilot, so
    /// this puts it back on — down START's own resume branch, so there is one way to pick a stopped run up and not
    /// two. Leaving him to press START himself would cost a second trip out of EVE for a pause he never asked for
    /// (Raymond, 2026-09-04).</summary>
    [RelayCommand]
    private async Task KeepRunAsync()
    {
        _pendingCopy = null;
        // Not an ObservableProperty, so the two things it alone decides say so themselves; RunState carries the rest.
        OnPropertyChanged(nameof(IsKeepRunButtonVisible));
        OnPropertyChanged(nameof(ClockHint));
        await StartRunAsync();
    }

    /// <summary>A saved run is committed; there is nothing left to throw away, and RunDiscard would not take it back
    /// either (ET-105 AC-1).</summary>
    public bool IsDiscardButtonVisible =>
        Authority.CanControl && RunState is ActivityRunState.Running or ActivityRunState.Stopped;

    /// <summary>Saving is every member's own, never the FC's alone: each pilot commits their own part of the run.
    /// It hangs on the state and not on whether a run row exists yet — a stopped run with nowhere to save to is a
    /// fault to report, not a button to hide.</summary>
    public bool IsSaveButtonVisible => RunState == ActivityRunState.Stopped;

    /// <summary>Why the controls are absent, when they are. Silence would be indistinguishable from a bug, and an
    /// unknown fleet boss is a state worth naming rather than an empty corner (ET-65 AC-7's rule, applied here).</summary>
    public bool IsCommandStatusShown => !Authority.CanControl;

    /// <summary>The authority's own sentence, except where it would be wrong: in a run whose clock is per pilot the
    /// pilot keeps START and STOP for their own leg, and only DISCARD is out of reach.</summary>
    public string CommandStatusText => !RunType.ClockPerPilot
        ? Authority.StatusText
        : Authority.Level switch
        {
            RunControlAuthorityLevel.Denied => (Authority.FleetCommanderName is { Length: > 0 } commander
                                                   ? $"Only {commander}, who commands this fleet,"
                                                   : "Only the fleet commander")
                                               + " can discard this run. Your own clock starts and stops with you.",
            RunControlAuthorityLevel.Unknown =>
                "Who commands this fleet is not known right now, so DISCARD is hidden. Your own clock starts and stops with you.",
            _ => Authority.StatusText
        };

    /// <summary>
    /// Why this run has no fleet while the pilot plainly has several. Only when both halves are true: several
    /// fleets in play <i>and</i> no fleet id came out of it — with a fleet settled there is nothing to report, and
    /// with one fleet there was never a question.
    /// </summary>
    public bool HasFleetNotice =>
        FleetId is null && (FleetsInPlay > 1 || UnstartedFleetName is not null || FormingFleetCount > 1);

    /// <summary>Says which way the run went and what would settle it. Not a warning about a fault: two started
    /// fleets is a legitimate state, and the window's job is to make the consequence visible rather than to refuse
    /// it.</summary>
    public string FleetNoticeText => UnstartedFleetName is { } fleetName
        ? $"'{fleetName}' has not been started, so this run is not shared. Start it to file this run under it."
        : FormingFleetCount > 1
            ? $"{FormingFleetCount} fleets are forming and none has started yet, so this run is not shared."
            : $"You are in {FleetsInPlay} started fleets at once, so this run belongs to none of them and is not shared. "
              // "Stop", not "conclude" (ET-166 follow-up): concluding is one-way, so a pilot who took this advice
              // literally threw away the recurring fleet it was only asking them to step out of for tonight.
              + "Stop the ones you are not flying to file it under one.";

    // ── The character column ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// My characters, in the order the pilot arranged them, as the switch between their runs (ET-164). Every
    /// registered character is here — not only the ones flying — so a resting toon can be switched to rather than
    /// having to be found somewhere else, and so the column does not change length under the pointer.
    ///
    /// It degenerates to one row instead of disappearing: until ET-130 there is at most one run on the clock, and a
    /// column that hid itself at n≤1 would be a column nobody ever saw.
    /// </summary>
    public ObservableCollection<RunCharacterRowViewModel> RunCharacters { get; } = [];

    public bool HasRunCharacters => RunCharacters.Count > 0;

    // ── Who was on the run ──────────────────────────────────────────────────────────────────────────

    public ObservableCollection<RunParticipantViewModel> Participants { get; } = [];

    /// <summary>Who this window has actually heard from, one row per member that sent a sample. Never a roster:
    /// nothing here can see a member who is not sharing, which is what the FLEET section says under it.</summary>
    public ObservableCollection<ActivityFleetMemberViewModel> FleetMembers { get; } = [];

    /// <summary>What the figures were counted over. "sharing a location" read as "they are in the same place", which
    /// is the very question a pilot asks this chip — under it stood RaymondKrah in Amarr and Jithran in Shaggoth
    /// (Raymond, 2026-09-03). Each member shares theirs, and that is all this counts.</summary>
    public string FleetStatusText => FleetMemberCount > 1
        ? _IsInPocket
            ? $"based on {AnchoredFleetMemberCount} of {FleetMemberCount} members sharing their location"
            : $"based on {FleetMemberCount} members sharing their location"
        : "no other member has reported in yet";

    /// <summary>
    /// Whether there is a fleet to show at all. Nothing here may claim "solo": the window is never told the pilot
    /// is alone, it is only ever told about a fleet — by the commander's own start (which sets
    /// <see cref="FleetId"/>) or by a member's sample arriving on the bus. Without either, the section is not
    /// collapsed but gone, because an empty FLEET section reads as a measurement and it is not one.
    /// </summary>
    public bool IsFleetShown =>
        FleetId is not null || _fleetLocations.Count > 0 || _fleetIsk.Count > 0 || Participants.Count > 0
        || FleetMembers.Count > 0;

    public string RunOriginText => RunState switch
    {
        ActivityRunState.NotStarted => "not started",
        ActivityRunState.Discarded => "discarded by the fleet commander",
        _ => _startedOnEntry ? "started on entry" : _isManualRun ? "manual" : "estimated from fleet"
    };

    /// <summary>
    /// What the clock does not say on its face. <c>AbyssalSpace.Describe</c> writes a "+" for this; here it is a
    /// sentence under the figure instead, which is where it ended up after the first round of review.
    ///
    /// In a pocket it is this pilot's own twenty minutes (ET-243): each pilot's pocket collapses on its own clock, so
    /// the fleet's earliest entry is the FLEET line's figure and never this one's.
    /// </summary>
    public string ClockHint => _IsInPocket
        ? "Your own twenty minutes, from your own way in. The clock is a floor — the moment of entry cannot be "
          + "observed, so this is at most what is left."
        : _pendingCopy is { } waiting
            ? $"{waiting.Name} is copied and waiting. Save or discard this {SignatureName} run and it takes over; "
              + "KEEP drops the copy and puts the clock back on this run."
            : RunState == ActivityRunState.Stopped
                // STOP is a pause, not an end (Raymond, 2026-09-02): stepping out mid-site and coming back has to
                // cost you nothing, so START picks the same run back up. What ends a run is SAVE or DISCARD.
                ? "Stopped runs keep their figures; start picks this run back up."
                : "Manual start and stop are the only source for this run.";

    /// <summary>Whether the pilot is in a pocket right now: what ESI last saw, and before it has seen anything, what
    /// the run's type says.</summary>
    public bool IsInsideAbyssal => InsideAbyssal ?? _IsInPocket;

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Restore the remembered weather and tier, find out whose run this is, and attach to the run that is
    /// already running if there is one. Separate from the constructor so the window can be built synchronously and a
    /// test can assert the round-trip without racing anything.</summary>
    public async Task LoadAsync()
    {
        // Same guard _AdoptRunningRunAsync already carries: with no dispatcher there is nothing remembered to
        // restore, and that is a window without a store rather than a fault.
        IReadOnlyList<SettingDto>? settings = null;
        if (_services.GetService<CqrsDispatcher>() is not null)
        {
            using var scope = _services.CreateScope();
            settings = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Query(new GetSettingsQuery());
        }

        foreach (RunWindowSection section in _AllSections())
            section.Load(settings);
        await _ResolveCharacterAsync(mayAsk: false);
        await _LoadRunCharactersAsync();
        await _AdoptRunningRunAsync();
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(DateTime.UtcNow);
        // After Refresh, never before it: that is where the authority is worked out, and START is gated on it.
        await _StartOnArrivalAsync();
    }

    // The window being open or closed decided which of two routes a copied signature took, and fixing only one of
    // them is what kept ET-100 alive through four attempts. Both routes come here, and both come here only after
    // the state they settle, so this sees the window a pilot would have been looking at.
    private async Task _StartOnArrivalAsync()
    {
        // Deliberately the START button's own condition rather than a copy of it: a group run left standing owns
        // this window until the FC decides, and an automatic start must not reach past that either (ET-105 AC-1).
        if (!StartsOnArrival || !IsStartButtonVisible)
            return;

        try
        {
            await StartRunAsync();
        }
        catch (Exception ex)
        {
            // One caller is a void hand-over and the other an async void OnOpened, so an escape here is an
            // unobserved task or a crash on the UI thread. Same treatment the signature hand-over gives its own.
            _services.GetService<IToastService>()?.Show("Run not started",
                $"Could not start the run on {SignatureName}: {ex.Message}", ToastKind.Error);
            _SignatureDecision($"the automatic start failed: {ex.Message}", SignatureName ?? "(no site)");
        }
    }

    /// <summary>
    /// Whose run this is. The registry has no "active character" by design — an action picks one at the moment it
    /// happens — so one local character answers it outright and several ask, once, at START. Everything the window
    /// then attributes to a pilot (bounties, location, enemies, the stored run) hangs off this one answer.
    /// </summary>
    private async Task<bool> _ResolveCharacterAsync(bool mayAsk)
    {
        if (_runCharacterId is not null)
            return true;

        if (_services.GetService<ICharacterRegistry>() is not { } registry)
            return false;

        List<Character> known = (await registry.GetAllAsync())
            .Where(character => character.EsiCharacterId is not null)
            .ToList();

        // Only the pilots actually at the keyboard can be flying this site, so only they are worth asking about.
        // Raymond has three characters registered and one EVE client open, and was still asked which of the three
        // it was (2026-09-02). Seeing none is not knowing rather than nobody, and START cannot proceed without a
        // character at all, so then the whole list stands and the question is still worth asking.
        List<Character> candidates = InGameCharacters.Among(known, _services.GetService<ILocalCharacterPresence>());
        if (candidates.Count == 0)
            candidates = known;

        Character? chosen = candidates is [{ } only] ? only : null;
        if (chosen is null && mayAsk && candidates.Count > 1
            && _services.GetService<IDialogService>() is { } dialogs)
        {
            // Multi-select (ET-210): multiboxing several of these candidates on the same site is exactly as real a
            // case as flying one, so the same question offers the same answer's plural. One ticked box behaves
            // exactly like the single picker used to (AC-3) — the rest, if any, ride along as UseAdditionalCharacters
            // and get their own run under this one's group code once _StoreRunAsync has a row to share it from.
            IReadOnlyList<int>? picked = await dialogs.PickCharactersAsync("Whose run is this?",
                [.. candidates.Select(character => new CharacterPickOption(
                    character.EsiCharacterId!.Value, character.Name, "local character", Enabled: true))]);
            if (picked is { Count: > 0 })
            {
                chosen = candidates.FirstOrDefault(character => character.EsiCharacterId == picked[0]);
                UseAdditionalCharacters([.. picked.Skip(1)
                    .Select(id => candidates.FirstOrDefault(character => character.EsiCharacterId == id))
                    .Where(character => character is not null)
                    .Select(character => (character!.EsiCharacterId!.Value, character.Name))]);
            }
        }

        if (chosen is null)
            return false;

        _runCharacterId = chosen.EsiCharacterId;
        _runCharacterName = chosen.Name;
        return true;
    }

    // Characters picked alongside the acting one (ET-210): each gets its own Run row, sharing this window's
    // GroupCode, once _StoreRunAsync knows one. The window itself still shows one acting pilot — the chip in the
    // header is what says the run also covers these.
    private IReadOnlyList<(int Id, string Name)> _additionalCharacters = [];

    /// <summary>Remember characters to also start a run for, alongside the acting one <see cref="UseCharacter"/>
    /// settles. Set before the window's own run is stored — <see cref="_StoreRunAsync"/> is the only reader.</summary>
    public void UseAdditionalCharacters(IReadOnlyList<(int Id, string Name)> characters) =>
        _additionalCharacters = characters;

    /// <summary>
    /// Attach to the run the store already has open, rather than opening a second one beside it. Reopening the
    /// window mid-run, or opening it after one was left unsaved, must land on the same row the loot is filed under —
    /// two rows running at once is exactly the state <c>RunningRunLookup</c> refuses to guess between.
    /// </summary>
    private async Task<bool> _AdoptRunningRunAsync()
    {
        if (_services.GetService<CqrsDispatcher>() is null)
            return false;

        using var scope = _services.CreateScope();
        // Scoped to this pilot's own run once one is settled (ET-130 deel 2) — six toons on six sites are six
        // independent counts of one, not one count of six. A window that does not know its pilot yet (a fleet-run
        // offer accepted with no picker shown, because too few clients were up to ask) still falls back to the old
        // app-wide count: with only one run anywhere, that one is unambiguous regardless of whose it is, and
        // _AdoptCharacterAsync below learns the pilot FROM the row it adopts. The kind comparison is identity, kept on
        // purpose (ET-236): a window only ever takes over a run of the kind it was opened as.
        Result<RunningRunDto> running = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Query(new GetRunningRunQuery(_runCharacterId, _targetRunId));
        if (!running.IsSuccess || running.Value is not { } run || run.ActivityKind != Kind)
            return false;

        // This window was opened on a signature, and the run still open is for a different site. That run is over:
        // it is closed out here and now, and the window comes up clean on the site actually copied. Closing out is
        // DiscardRunCommand, which stops the activity and unlinks the group code and "never removes a row, a loot
        // capture or a bounty" — the run keeps everything it collected and stays in the store.
        //
        // Only a run of this pilot's own. One that belongs to a group is left standing and waits for a decision:
        // ending that one reaches every other member's machine, and that is the FC's button to press, not this
        // window's (ET-105 AC-1).
        if (SignatureName is { Length: > 0 } copied
            && !_IsSameRun(run.Signature, run.SiteName, SignatureId, copied))
        {
            // The run's OWN group code decides this, and nothing else. It used to also require `FleetId is null` —
            // this window's live fleet membership — which is a different question about a different thing: a Run row
            // has no fleet id at all, so its group code is the only tie it has to anybody else. Being in a fleet
            // tonight does not hand yesterday's solo run to whoever commands tonight (ET-152: "the fleet id says
            // where a run is filed, not who commands it").
            //
            // What that cost Raymond on 2026-09-04: a run of his own left open since the previous day was adopted,
            // refused close-out because he happened to be in Jithran's fleet, and then read as a group run — so the
            // window told him only Jithran could stop or discard it. His own run, and no way out of it.
            if (run.GroupCode is null)
            {
                await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                    .Send(new DiscardRunCommand(run.Id, DateTime.UtcNow));
                _SignatureDecision($"closed out the {run.SiteName} run left open in the store", copied);
                return false;
            }

            _pendingCopy = new PendingCopy(Kind, copied, StartsOnArrival, SignatureId, SignatureGroup, MatchedSites,
                null, null, null, []);
            SignatureId = run.Signature;
            SignatureGroup = run.SignatureGroupSnapshot;
            SignatureName = run.SiteName;
            MatchedSites = [];
            _SignatureDecision("the run left open belongs to a group, so it waits", copied);
        }
        else if (SignatureName is not { Length: > 0 })
        {
            // No signature was copied into this window at all — a manual START (ET-163) or the runs-overview
            // lane — so the run being adopted is the only source for what site this is. The mismatch branch
            // above already carries this for the run it closes out or waits on; this is the same field for
            // the plain case, which used to leave SITE reading "not known yet" for a site the store already had.
            SignatureId = run.Signature;
            SignatureGroup = run.SignatureGroupSnapshot;
            SignatureName = run.SiteName;
        }

        RunId = run.Id;
        AnchorUtc = run.StartedAtUtc;
        // A window resuming a specific stopped run (ET-254, _targetRunId) comes up exactly as if its own pilot had
        // pressed STOP and reopened it: paused, not ticking. StartRunAsync's own "RunId is not null && RunState is
        // Stopped" branch is what actually resumes it — _StartOnArrivalAsync (via StartsOnArrival) is what this
        // window is opened with to fire that branch the moment it loads, rather than leaving START for a second
        // click. Every other caller of this method still only ever sees a Running run, so StoppedAtUtc stays null.
        StoppedAtUtc = run.StoppedAtUtc;
        await _JoinStoredRunToFleetGroupAsync(scope, run);
        GroupCode ??= run.GroupCode;
        _isManualRun = true;
        RunState = run.StoppedAtUtc is null ? ActivityRunState.Running : ActivityRunState.Stopped;
        await _AdoptCharacterAsync(checked((int)run.CharacterId));
        _OnRunWatched();

        // ET-252: this window is not the one that started the run, so MISSION has nothing of its own to show
        // unless the agent, level, system and the reward lines already on the row travel with the adoption —
        // otherwise it reads the agent as unstated and every reward as never recorded, even though the run itself
        // has carried them since the moment it started. Skipped on the way to a waiting copy just below: that
        // window is about to show a DIFFERENT mission's facts, not this retired run's.
        if (RunType.HasAgent && _pendingCopy is null)
        {
            MissionAgentId = run.AgentId;
            MissionLevel = run.MissionLevel;
            MissionSolarSystemId = run.SolarSystemId;
            PendingParameters = [.. (run.Parameters ?? []).Select(parameter => new RunParameterInput
            {
                ParameterKey = parameter.ParameterKey,
                TypedValue = parameter.TypedValue,
                Amount = parameter.Amount,
                ItemTypeId = parameter.ItemTypeId,
                BonusWindowSeconds = parameter.BonusWindowSeconds,
                ObservedAtUtc = parameter.ObservedAtUtc
            })];
        }

        // After the state above: StopRun only acts on a run it considers running.
        if (_pendingCopy is not null)
            StopRun(DateTime.UtcNow);

        // ET-254: a deliberate resume — RESUME in the UNFINISHED band, or the startup notice offering the same
        // choice — picks the clock back up the moment this window has the row, rather than leaving START for a
        // second click nobody asked this caller to make the pilot take. Every other caller of this method leaves
        // _targetRunId null and never reaches here.
        if (_targetRunId is not null && run.StoppedAtUtc is not null)
        {
            // Own toons (ET-210) resume as a group: _ResumeAdoptedRunAsync's own _SetStoredRunStoppedAsync loops
            // over Participants to pick up every sibling sharing this GroupCode, own-toon ones included — but this
            // window has never ticked before, so Participants is still empty unless asked for outright first. The
            // window that already had this run open never had this problem: a pilot's own STOP/START is always
            // asked on a window whose Participants an earlier tick already filled in.
            await _RefreshParticipantsAsync();
            await _ResumeAdoptedRunAsync(DateTime.UtcNow);
        }

        return true;
    }

    /// <summary>The resume half of the STOP/START pause (Raymond, 2026-09-02: "stepping out of a site halfway and
    /// pressing START again must cost you neither your enemies nor your loot"), shared by <see cref="StartRunAsync"/>'s
    /// own button and <see cref="_AdoptRunningRunAsync"/>'s deliberate resume (ET-254) — the same pause, picked back
    /// up by two different callers, neither of which may drift from the other.</summary>
    private async Task _ResumeAdoptedRunAsync(DateTime nowUtc)
    {
        await _SetStoredRunStoppedAsync(null);
        StoppedAtUtc = null;
        CorrectedStopUtc = null;
        RunState = ActivityRunState.Running;
        _OnRunWatched();
        // A run whose clock is per pilot (ET-243) has no group-wide resume — only this pilot's own leg is picked
        // back up, and only their own announcement says so (ET-250): the old fleet.run-group would also read as
        // this pilot's fresh start to FleetRunGroupCodeCoordinator, and fleet.run-group.pilot-stopped is read the
        // other way around, so a resume needed a type of its own.
        if (RunType.ClockPerPilot && _runCharacterId is { } own)
            _AnnouncePilotResumeToFleet(own, nowUtc);
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(nowUtc);
    }

    /// <summary>
    /// Put the run this window just adopted into the fleet's group. Adopting publishes no <c>RunStartedEvent</c>,
    /// so <c>FleetRunGroupCodeCoordinator</c> never hears of it and the stored row would stay outside the group the
    /// window says it is in — a member who was already flying would join on screen only. Joining is an explicit
    /// act, so the row is relinked: unlink first, because a run cannot be in two groups and
    /// <c>LinkRunToGroupCode</c> refuses an occupied one.
    /// </summary>
    private async Task _JoinStoredRunToFleetGroupAsync(IServiceScope scope, RunningRunDto run)
    {
        // A run the signature check above decided is a DIFFERENT site is on its way to being stopped, so it does not
        // join this fleet's group — it was never this window's run.
        if (_pendingCopy is not null)
            return;

        if (GroupCode is not { } fleetGroupCode || string.Equals(run.GroupCode, fleetGroupCode, StringComparison.Ordinal))
            return;

        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        if (run.GroupCode is not null)
            await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(run.Id));
        await dispatcher.Send(new LinkRunToGroupCodeCommand(run.Id, fleetGroupCode, FleetId));
    }

    /// <summary>Name the pilot before the window loads, for a caller that already asked — the fleet-run offer, when
    /// several clients are up. <see cref="_ResolveCharacterAsync"/> then has its answer and asks nobody. A run
    /// already on the clock still wins: <see cref="_AdoptRunningRunAsync"/> takes that run's character instead,
    /// because a run belongs to whoever started it.</summary>
    public void UseCharacter(int characterId, string characterName)
    {
        _runCharacterId = characterId;
        _runCharacterName = characterName;
    }

    /// <summary>The specific run <see cref="_AdoptRunningRunAsync"/> must adopt, Stopped included (ET-254) — set
    /// before <see cref="LoadAsync"/> by a caller that already knows which row it wants: RESUME in the UNFINISHED
    /// band, or the startup notice offering the same choice. Every other caller leaves this null and keeps today's
    /// "the one run running for this pilot, or none" question — naming a row outright is for a caller that already
    /// knows the answer, not a second way to ask the same one.</summary>
    private Guid? _targetRunId;

    /// <summary>Names the run this window is opened to resume. The moment <see cref="_AdoptRunningRunAsync"/> has
    /// it, the clock is picked back up on its own — exactly as if this run's own pilot had pressed STOP and then
    /// START again, because that is what RESUME is (the same pause <c>ClockHint</c> already describes for the
    /// in-window case). <see cref="UseCharacter"/> is the caller's to call alongside this, same as every other
    /// opener that already knows its pilot.</summary>
    public void ResumeRun(Guid runId) => _targetRunId = runId;

    /// <summary>The pilot this window has been given or has settled on, so a hand-over to a window that is already
    /// up can carry it (<c>DialogService.ShowActivityWindow</c>). Without this the caller could ask before opening
    /// and still have the open window ask a second time — the half of the two routes that ET-158's AC-5 is about,
    /// and the half that fixing only one of them left broken four times over.</summary>
    public (int Id, string Name)? PickedCharacter =>
        _runCharacterId is { } id && _runCharacterName is { } name ? (id, name) : null;

    /// <summary>
    /// Take over the run the fleet commander announced. Joining used to be the group code and the fleet id and
    /// nothing else, so a member landed on a brand-new window that was NOT STARTED while the commander's clock had
    /// been going for minutes (Raymond, 2026-09-03).
    ///
    /// Everything the joining window is missing travels on the announcement, so nothing is fetched: the commander's
    /// run row lives in a database this client cannot read, and asking the server for a second copy of facts already
    /// in hand is a second source to keep in step. The site name and the scan id are only taken where this window has
    /// none — a member who copied a signature of their own keeps it, and <c>_AdoptRunningRunAsync</c> below decides
    /// between the two runs.
    ///
    /// A run whose clock is per pilot (ET-246) is joined armed instead — prepared or already started alike: the
    /// commander's moment is his own way in, not this pilot's, so nothing starts and no row is made until this pilot
    /// goes in or presses START.
    /// </summary>
    public void JoinFleetRun(RunGroupCodeStart start)
    {
        GroupCode = start.GroupCode;
        FleetId = start.FleetId;
        // The commander's scan id names the same signature on this member's own scanner — the id belongs to the
        // system, not to the pilot (ET-151) — so LOCATION reads RUS-326 · Shousran here too instead of the bare
        // system it showed a member while the commander had the site.
        SignatureId ??= start.Signature;
        // Same rule, same reason (ET-239): a member who already copied their own signature keeps the type it
        // resolved to; only a member with none reads the commander's.
        SignatureGroup ??= start.SignatureGroupSnapshot;
        // Unlike the two lines above, this is never "only where empty" (ET-241): the commander's own answer always
        // wins over whatever this member's window already picked up from its own remembered settings — an unrelated
        // abyssal's leftover tier and weather, which reads as established when it is really just a stale default
        // (the trap ET-208 decision 3 names). A member's own later, deliberate pick through the ACTIVITY section
        // still stands: this only runs once, when the commander's own start reaches this window.
        if (start.AbyssalTierIndex is { } tierIndex)
            TierIndex = tierIndex;
        if (AbyssalWeather.IndexOf(start.AbyssalWeatherName) is { } weatherIndex)
            WeatherIndex = weatherIndex;

        if (RunType.ClockPerPilot)
        {
            Refresh(DateTime.UtcNow);
            return;
        }

        AnchorUtc = start.StartedAtUtc;
        StoppedAtUtc = null;
        RunState = ActivityRunState.Running;
        _OnRunWatched();
        // A joined run still needs its own row, or this member's loot and bounties have nothing to hang off.
        _ = _BeginEstimatedRunAsync(start.StartedAtUtc, start.SiteName);
        Refresh(DateTime.UtcNow);
    }

    /// <summary>The commander started, and this window was already open on nothing. Only a start that came from the
    /// commander joins a window: a member's own start is announced too, and it is not an invitation. The kind
    /// comparison is identity, kept on purpose (ET-236): a window only joins a run of the kind it was opened as.
    ///
    /// Under this window's own code, in a run whose clock is per pilot, every start is a leg of the fleet's clock
    /// instead (ET-243) — a member's as much as the commander's — and a window armed on one run joins no other.</summary>
    private void _OnFleetRunStarted(FleetRunGroupCodeEvent integrationEvent)
    {
        RunGroupCodeStart start = integrationEvent.Data;
        if (RunType.ClockPerPilot && GroupCode is { } groupCode)
        {
            if (string.Equals(groupCode, start.GroupCode, StringComparison.Ordinal))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _RefreshFleetClock(DateTime.UtcNow));
            return;
        }

        if (!start.IsFleetCommander || RunState != ActivityRunState.NotStarted
            || (FleetId is { } fleetId && fleetId != start.FleetId) || start.ActivityKind != Kind)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => JoinFleetRun(start));
    }

    /// <summary>The commander set a run up before anyone went in (ET-246), and this window is open on nothing: it arms
    /// on it, the same way it would join his start. Only a type whose clock is per pilot is ever prepared.</summary>
    private void _OnFleetRunPrepared(FleetRunGroupPreparedEvent integrationEvent)
    {
        RunGroupCodeStart start = integrationEvent.Data;
        if (!start.IsFleetCommander || !RunType.ClockPerPilot || RunState != ActivityRunState.NotStarted
            || GroupCode is not null || (FleetId is { } fleetId && fleetId != start.FleetId)
            || start.ActivityKind != Kind)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => JoinFleetRun(start));
    }

    /// <summary>In a run whose clock is per pilot the commander's STOP ends his own leg, never this one (ET-243) — an
    /// older commander's client still sends it, and it is ignored here: only this pilot's own way out, or their own
    /// STOP, stops their clock.</summary>
    private void _OnFleetRunStopped(FleetRunStoppedEvent integrationEvent)
    {
        RunGroupStop stop = integrationEvent.Data;
        if (RunType.ClockPerPilot || GroupCode is not { } groupCode
            || !string.Equals(groupCode, stop.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => StopRun(stop.StoppedAtUtc));
    }

    /// <summary>One pilot of this run came out (ET-243). Nothing of this window's own stops; the fleet's clock learns
    /// who is still in.</summary>
    private void _OnFleetPilotStopped(FleetRunPilotStoppedEvent integrationEvent)
    {
        if (GroupCode is not { } groupCode
            || !string.Equals(groupCode, integrationEvent.Data.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _RefreshFleetClock(DateTime.UtcNow));
    }

    /// <summary>One pilot of this run picked their own leg back up after their own STOP (ET-250). Nothing of this
    /// window's own changes; the fleet's clock learns they are in again.</summary>
    private void _OnFleetPilotResumed(FleetRunPilotResumedEvent integrationEvent)
    {
        if (GroupCode is not { } groupCode
            || !string.Equals(groupCode, integrationEvent.Data.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _RefreshFleetClock(DateTime.UtcNow));
    }

    /// <summary>
    /// The commander threw the run away, and this is a member's window. It stays open and says so (ET-155): the
    /// member is the one this happened to rather than the one who did it, and a toast is gone in seconds while he may
    /// only look at the window minutes later. He closes it himself; nothing here closes it for him.
    ///
    /// The clock comes to rest and nothing is taken away — which is what the commander's own confirmation promises
    /// the members, so the row he already has stays exactly where it is and the notice does not contradict it. The
    /// stored row is <c>FleetRunGroupCodeCoordinator</c>'s to unlink; it does that on every client.
    /// </summary>
    private void _OnFleetRunDiscarded(FleetRunDiscardedEvent integrationEvent)
    {
        RunGroupDiscard discard = integrationEvent.Data;
        if (_isDiscarding || GroupCode is not { } groupCode
            || !string.Equals(groupCode, discard.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Armed and never gone in (ET-246): there is no run of this pilot's to end, so the window goes back to
            // being armed on nothing and says what happened — a Discarded state would claim a run that never was.
            if (RunState is ActivityRunState.NotStarted && RunId is null)
            {
                GroupCode = null;
                RunNoticeText = "The fleet commander called this run off before you went in. Nothing of yours was "
                                + "recorded. Close this window, or fly the pocket on your own.";
                Refresh(DateTime.UtcNow);
                return;
            }

            StoppedAtUtc ??= discard.DiscardedAtUtc;
            RunState = ActivityRunState.Discarded;
            _OnRunClosed();
            GroupCode = null;
            RunNoticeText = "The fleet commander discarded this run, so it is no longer part of the group. "
                            + "Nothing you already saved is gone. Close this window when you have read it.";
            if (RunLoot is not null)
                _ = RunLoot.RefreshAsync();
            Refresh(DateTime.UtcNow);
        });
    }

    /// <summary>The commander changed the pocket's tier or weather after this member already joined (ET-241) — the
    /// same two facts <see cref="JoinFleetRun"/> takes at the start, kept in step for as long as the run runs.
    /// Unconditional, same reasoning as the join itself: the commander's own answer always wins here.</summary>
    private void _OnFleetAbyssalUpdated(FleetRunGroupAbyssalUpdatedEvent integrationEvent)
    {
        RunGroupAbyssalUpdate changed = integrationEvent.Data;
        if (GroupCode is not { } groupCode || !string.Equals(groupCode, changed.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TierIndex = changed.TierIndex;
            WeatherIndex = AbyssalWeather.IndexOf(changed.WeatherName);
            Refresh(DateTime.UtcNow);
        });
    }

    /// <summary>The stored run names its character by id; the gamelog knows pilots by name. Both are needed, so the
    /// id is taken back through the registry rather than left half-resolved.</summary>
    private async Task _AdoptCharacterAsync(int characterId)
    {
        if (_runCharacterId == characterId && _runCharacterName is not null)
            return;

        _runCharacterId = characterId;
        if (_services.GetService<ICharacterRegistry>() is { } registry)
            _runCharacterName = (await registry.GetAllAsync())
                .FirstOrDefault(character => character.EsiCharacterId == characterId)?.Name ?? _runCharacterName;
    }

    /// <summary>
    /// Whose window this is. The run's character once START has resolved one, and before that the character this
    /// client is publishing fleet metrics as — the same membership set the FLEET section beside it is drawn from,
    /// since <see cref="FleetMetricPublisher"/> puts a sample on the bus for every (character, fleet) in it.
    ///
    /// Not <see cref="IActiveFleetState"/>, which this used to ask: that is the fleet you last selected in the
    /// fleets window and only an explicit <c>Enter</c> fills it, so on a client that never opened that window it is
    /// empty while the FLEET section is listing members — which is exactly how the FIT section came to print a
    /// question it had no one left to ask. The server dropped the same Enter-driven model for membership
    /// (<c>FleetBroadcastResolver</c>) and the publisher followed; this is the window catching up.
    ///
    /// Null when several of this client's characters are in fleets at once. That is a real question rather than a
    /// gap, and START is where it gets asked.
    /// </summary>
    private int? _ActingCharacterId()
    {
        if (_runCharacterId is { } resolved)
            return resolved;

        IEnumerable<FleetParticipant> mine = _Participation();
        if (FleetId is { } fleetId)
            mine = mine.Where(participant => participant.FleetId == fleetId);
        return mine.Select(participant => participant.CharacterId).Distinct().ToList() is [{ } only] ? only : null;
    }

    /// <summary>
    /// Which fleet this run belongs to — the mirror image of <see cref="_ActingCharacterId"/>: there the fleet
    /// narrows the character, here the character narrows the fleet.
    ///
    /// Membership, not the fleets window's selection. <c>IActiveFleetState</c> is only ever filled by an explicit
    /// <c>Enter</c>, and the two production calls to it both sit behind OPEN METRICS — so a fleet commander who never
    /// pressed that button had no fleet id, and with it no group code and no announcement to his fleet (ET-152).
    ///
    /// Null while several fleets are in play at once, the same way the character is null while several are: that is a
    /// question rather than a gap. It is not answered here — this window cannot pick which of a pilot's fleets a run
    /// belongs to — but it is no longer swallowed either: the count goes to <see cref="FleetsInPlay"/> and the
    /// window says why the run went solo (ET-165).
    /// </summary>
    private static long? _ActingFleetId(List<long> myFleetIds) => myFleetIds is [{ } only] ? only : null;

    /// <summary>
    /// Every fleet this window's pilot is in right now. One list rather than a count beside a pick, so
    /// <see cref="_ActingFleetId"/> and <see cref="FleetsInPlay"/> can never disagree about how many there were —
    /// a notice explaining a state the window is not in would be worse than the silence it replaces.
    /// </summary>
    private List<long> _MyFleetIds()
    {
        IEnumerable<FleetParticipant> mine = _Participation();
        if (_runCharacterId is { } characterId)
            mine = mine.Where(participant => participant.CharacterId == characterId);
        return mine.Select(participant => participant.FleetId).Distinct().ToList();
    }

    private IReadOnlyList<FleetParticipant> _Participation() =>
        _services.GetService<IFleetParticipation>()?.Current ?? [];

    /// <summary>
    /// Put a name to <see cref="_ActingCharacterId"/> for the header. Clock-driven like everything else here, but it
    /// only reads the registry when the answer has actually changed — the id is settled once and then holds for the
    /// rest of the run, so this is a lookup per run rather than one per second.
    /// </summary>
    private async Task _RefreshActingCharacterAsync()
    {
        if (_ActingCharacterId() is not { } characterId)
        {
            ActingCharacterName = null;
            _namedCharacterId = null;
            return;
        }

        if (_namedCharacterId == characterId)
            return;

        // The run's own character already carries its name; anyone else has to be looked up once.
        string? name = _runCharacterId == characterId && _runCharacterName is not null
            ? _runCharacterName
            : _services.GetService<ICharacterRegistry>() is { } registry
                ? (await registry.GetAllAsync()).FirstOrDefault(c => c.EsiCharacterId == characterId)?.Name
                : null;

        if (name is null)
            return; // leave it unnamed and try again next tick rather than caching a miss.

        _namedCharacterId = characterId;
        ActingCharacterName = name;
    }

    /// <summary>
    /// Fill the character column and fetch each portrait once. Two sources, the same pair the main window's
    /// character list merges: the registry in the pilot's own order, then the names only a gamelog has ever
    /// mentioned. The second source is the whole point — the registry cannot hold a character without an
    /// <c>EsiCharacterId</c> at all, so without it the unlinked pilot could never appear in this column.
    /// </summary>
    private async Task _LoadRunCharactersAsync()
    {
        if (_services.GetService<ICharacterRegistry>() is not { } registry)
            return;

        RunCharacters.Clear();
        foreach (Character character in await registry.GetAllAsync())
            RunCharacters.Add(new RunCharacterRowViewModel(character));

        IEnumerable<string> unlinked = _services.GetService<GamelogWatcherService>()?.ObservedCharacters ?? [];
        foreach (string name in unlinked.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            if (RunCharacters.All(row => !string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
                RunCharacters.Add(new RunCharacterRowViewModel(new Character(name)));

        OnPropertyChanged(nameof(HasRunCharacters));
        await _RefreshRunCharactersAsync();

        if (_services.GetService<ICharacterPortraitProvider>() is not { } portraits)
            return;

        foreach (RunCharacterRowViewModel row in RunCharacters)
            await row.LoadPortraitAsync(portraits);
    }

    /// <summary>Guards <see cref="_RefreshRunCharactersAsync"/> the same way <see cref="_isRefreshingParticipants"/>
    /// guards its participants counterpart: a slow query outliving one tick must not race the next tick's own read.</summary>
    private bool _isRefreshingRunCharacters;

    /// <summary>
    /// Put this window's state onto the column — which of my toons this window is showing (<see cref="IsSelected"/>),
    /// and, since ET-210, which ones actually have a run going right now regardless of which one is acting
    /// (<see cref="RunCharacterRowViewModel.HasRunningRun"/>). The second half needs the store: <c>GetRunningRunsQuery</c>
    /// reads every running run once a tick, one per character, the same source the RUNNING band in the runs
    /// overview already trusts for this (ET-203).
    /// </summary>
    private async Task _RefreshRunCharactersAsync()
    {
        if (RunCharacters.Count == 0)
            return;

        Dictionary<int, Guid> running = [];
        if (!_isRefreshingRunCharacters && _services.GetService<CqrsDispatcher>() is { } dispatcher)
        {
            _isRefreshingRunCharacters = true;
            try
            {
                using var scope = _services.CreateScope();
                Result<IReadOnlyList<RunningRunDto>> result = await scope.ServiceProvider
                    .GetRequiredService<CqrsDispatcher>().Query(new GetRunningRunsQuery());
                if (result.IsSuccess)
                    foreach (RunningRunDto run in result.Value!)
                        running[checked((int)run.CharacterId)] = run.Id;
            }
            finally
            {
                _isRefreshingRunCharacters = false;
            }
        }

        int? acting = _ActingCharacterId();
        foreach (RunCharacterRowViewModel row in RunCharacters)
        {
            row.IsSelected = row.IsEsiLinked && row.CharacterId == acting;
            row.RunId = row.IsEsiLinked && running.TryGetValue(row.CharacterId, out Guid runId) ? runId : null;
            row.HasRunningRun = row.RunId is not null;
            row.Attention = (row.IsSelected && row.HasRunningRun, IsClockCritical, IsClockWarning) switch
            {
                (true, true, _) => RunCharacterAttention.Critical,
                (true, _, true) => RunCharacterAttention.Warning,
                _ => RunCharacterAttention.None
            };
        }

        // The refusal changes with the run's state and now with every row's own, so both drive it.
        SelectRunCharacterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The immediate half of <see cref="_RefreshRunCharactersAsync"/> — <see cref="RunCharacterRowViewModel.IsSelected"/>
    /// only, which needs no query. Called right after this window's own acting character changes, so the column
    /// does not wait a whole tick to agree with the header.</summary>
    private void _RefreshRunCharacters()
    {
        int? acting = _ActingCharacterId();
        foreach (RunCharacterRowViewModel row in RunCharacters)
            row.IsSelected = row.IsEsiLinked && row.CharacterId == acting;
    }

    /// <summary>
    /// Switch the window to this character's own run — loot, bounty and every other section follow (ET-130 deel 3
    /// review finding, 2026-09-09: the column looked clickable and did nothing, so a run started for five toons at
    /// once had no way to register loot against any but the first). Refused for an unlinked character, and for one
    /// with no run of their own while this window's own run is going — there is nothing to switch it TO.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectRunCharacter))]
    private void SelectRunCharacter(RunCharacterRowViewModel row)
    {
        if (row.RunId is { } runId)
        {
            _ = _SwitchToRunAsync(row.CharacterId, row.Name, runId);
            return;
        }

        _runCharacterId = row.CharacterId;
        _runCharacterName = row.Name;
        _namedCharacterId = null;
        _ = _RefreshActingCharacterAsync();
        _RefreshRunCharacters();
    }

    private bool CanSelectRunCharacter(RunCharacterRowViewModel? row) =>
        row is { IsEsiLinked: true } && !row.IsSelected && (row.HasRunningRun || RunState is not ActivityRunState.Running);

    /// <summary>
    /// Re-point every character-specific section at a DIFFERENT run this pilot's own group already has going —
    /// GroupCode, FleetId and Participants stay put, because switching who the window shows is not leaving the
    /// group. Re-read from the store rather than trusted off the row: the run may have stopped or been saved in
    /// the moment between the click and this running, and a stale <see cref="RunCharacterRowViewModel.RunId"/> must
    /// not be adopted as if it still were live.
    /// </summary>
    private async Task _SwitchToRunAsync(int characterId, string name, Guid runId)
    {
        if (runId == RunId || _services.GetService<CqrsDispatcher>() is null)
            return;

        using var scope = _services.CreateScope();
        Result<RunningRunDto> result = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Query(new GetRunningRunQuery(characterId));
        // Identity, kept on purpose (ET-236): the column only switches between runs of the window's own kind.
        if (!result.IsSuccess || result.Value is not { } run || run.Id != runId || run.ActivityKind != Kind)
            return;

        _runCharacterId = characterId;
        _runCharacterName = name;
        _namedCharacterId = null;
        RunId = run.Id;
        AnchorUtc = run.StartedAtUtc;
        StoppedAtUtc = null;
        RunState = ActivityRunState.Running;
        _isManualRun = true;
        SignatureId = run.Signature;
        SignatureGroup = run.SignatureGroupSnapshot;
        SignatureName = run.SiteName;
        MatchedSites = [];
        // _enemyObservations is deliberately left standing (ET-210 review finding, 2026-09-09, third round):
        // clearing it here used to be the bug, not the fix. It used to be reset on every switch, on the reasoning
        // that it was "per character" — but it is a hand-typed count with nowhere else to live, and switching away
        // from the character it was started for and back again threw it out for good; Jithran's own enemies
        // vanished exactly that way. Bounty needs no such care any more (ET-219): it is written straight onto its
        // own run's RunBountyEntry rows as it comes in (GamelogClientService.AddBountyAsync), independently of
        // which character this window happens to be showing. Loot needs no reset either — it lives under the run's
        // own id in the store, and RunLoot re-reads it the moment RunId changes (OnRunIdChanged).
        await _AdoptCharacterAsync(characterId);
        await _RefreshActingCharacterAsync();
        _RefreshRunCharacters();
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Begin ticking. Separate from the constructor so a test drives <see cref="Refresh"/> with a clock it
    /// controls rather than racing a timer.</summary>
    public void Start()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh(DateTime.UtcNow);
        _timer.Start();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Work the whole readout out again. Public and clock-driven so the same code runs under a test as
    /// under the timer.</summary>
    public void Refresh(DateTime nowUtc)
    {
        _WriteHeartbeatIfDue(nowUtc);
        _RefreshLocation(nowUtc);
        _RefreshOwnPilotLegs(nowUtc);
        _RefreshClock(nowUtc);
        _RefreshArmed();
        _RefreshFleetClock(nowUtc);
        _ = RefreshFleetCommandAsync(nowUtc);
        // Before the summaries: a section's own Refresh is what settles this tick's readout (a mission's bonus
        // falling out of expiry, ET-237) — summarising first would describe last tick's answer instead of this one's.
        foreach (RunWindowSection section in _AllSections())
            section.Refresh(nowUtc);
        _RefreshGroupTotalIsk(nowUtc);
        _RefreshSummaries();
        _ = _RefreshActingCharacterAsync();
        _ = _RefreshRunCharactersAsync();
        _ = _RefreshParticipantsAsync();
        _ShareRunLootWithFleet();
    }

    private DateTime? _lastAliveWrittenAtUtc;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Writes <see cref="Entities.Run.LastAliveAtUtc"/> about once a minute while this run is on the clock (ET-254)
    /// — the one fact <c>StopRunsLeftRunningCommandHandler</c> can read, the next time the app starts, for when a
    /// run left running by a process that quit or crashed actually ended. Without it that handler's own restart
    /// moment was the only candidate, which for an app closed overnight read a run as twelve hours long.
    ///
    /// Throttled here rather than in the handler: the clock already ticks every second (<see cref="Refresh"/>), and
    /// a write that fine-grained would be all cost for a precision nothing downstream uses — a stop time honest to
    /// the minute is the whole of what this ticket asked for.
    /// </summary>
    private void _WriteHeartbeatIfDue(DateTime nowUtc)
    {
        if (RunId is not { } runId || RunState is not ActivityRunState.Running
            || _services.GetService<CqrsDispatcher>() is null)
            return;

        // No write yet this run measures from AnchorUtc, not from now — the clock's own start, not the moment this
        // method happens to be asked. Without that a run just started, adopted or resumed would write on its very
        // next tick (elapsed since last write: zero, not "under a minute"), which is both an unneeded write and a
        // race against whatever else that same start already touched this row for the same instant.
        DateTime baseline = _lastAliveWrittenAtUtc ?? AnchorUtc ?? nowUtc;
        if (nowUtc - baseline < HeartbeatInterval)
            return;

        _lastAliveWrittenAtUtc = nowUtc;
        _ = _WriteHeartbeatAsync(runId, nowUtc);
    }

    private async Task _WriteHeartbeatAsync(Guid runId, DateTime atUtc)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new TouchRunAliveCommand(runId, atUtc));
    }

    /// <summary>
    /// Offer what this run has looted to the fleet. Clock-driven like the rest of the window, and it only ever hands
    /// the figure to the metric source: what leaves the machine is the publisher's share gate, where loot is opt-IN,
    /// so nothing here decides who may see a pilot's ISK.
    ///
    /// The figure is <see cref="RunLootViewModel.NetIsk"/> — the same one the LOOT section shows, priced from the
    /// market cache by type id — so nothing is valued twice and the fleet sees what the pilot sees. Bounty needs no
    /// counterpart here: the gamelog has been putting it on this stream per fleet run all along.
    /// </summary>
    private void _ShareRunLootWithFleet()
    {
        if (_ActingCharacterId() is not { } characterId
            || _services.GetService<RunLootMetricSource>() is not { } source)
            return;

        source.SetLootIsk(characterId, RunState is ActivityRunState.NotStarted ? null : RunLoot?.NetIsk);
    }

    /// <summary>The button. Creating the stored run is the whole of it — without that row there is no run for the
    /// loot, the bounties or the enemies to hang off, and the clock would be counting on its own.
    ///
    /// It picks up the run the store already has open rather than opening a second one beside it, and it resets
    /// nothing: STOP is a pause, so stepping out of a site halfway and pressing START again must cost you neither
    /// your enemies nor your loot, your bounty, your fit or your times (Raymond, 2026-09-02). What ends a run is
    /// SAVE or DISCARD.</summary>
    [RelayCommand]
    private async Task StartRunAsync()
    {
        DateTime nowUtc = DateTime.UtcNow;
        // The row this window stopped is picked back up rather than a second one opened beside it. Adopt cannot do
        // it any more — a stopped run is exactly what it must not hand a fresh window — so the pause is resumed here,
        // where the run id is still known.
        if (RunId is not null && RunState is ActivityRunState.Stopped)
        {
            await _ResumeAdoptedRunAsync(nowUtc);
            return;
        }

        // Tried both before and after resolving a pilot: with one already settled this is the only call that ever
        // runs, and it is not asked twice. With none settled yet, asking first and then trying again catches a run
        // this now-known pilot already has open — without it, START would blindly file a second row under the
        // character just picked, right beside the one they left running (ET-130 deel 2).
        if (await _AdoptRunningRunAsync())
        {
            if (RunLoot is not null)
                await RunLoot.RefreshAsync();
            Refresh(nowUtc);
            return;
        }

        if (!await _ResolveCharacterAsync(mayAsk: true))
        {
            _services.GetService<IToastService>()?.Show("Run not started",
                "No local character to file this run under. Add one first.", ToastKind.Error);
            return;
        }

        if (await _AdoptRunningRunAsync())
        {
            if (RunLoot is not null)
                await RunLoot.RefreshAsync();
            Refresh(nowUtc);
            return;
        }

        _startedOnEntry = false;
        StartManualRun(nowUtc);
        await _StoreRunAsync(nowUtc);
    }

    [RelayCommand]
    private void StopRun() => StopRun(DateTime.UtcNow);

    /// <summary>Move the window to a running run on the clock. <see cref="StartRunAsync"/> is what also gives it a
    /// row in the store; the way into a pocket calls this too, for a run nobody pressed a button for.</summary>
    public void StartManualRun(DateTime nowUtc)
    {
        AnchorUtc = nowUtc;
        StoppedAtUtc = null;
        // A stopped manual result is final; a later ESI anchor cannot reopen it.
        _isManualRun = true;
        BountyIsk = 0;
        RunState = ActivityRunState.Running;
        _OnRunWatched();
        OnPropertyChanged(nameof(IsStartButtonVisible));
        OnPropertyChanged(nameof(RunOriginText));
        Refresh(nowUtc);
    }

    /// <summary>Create the <c>Run</c> row this window writes to. The site's own facts travel with it, so a saved run
    /// still knows which signature it was flown on.</summary>
    private async Task _StoreRunAsync(DateTime startedAtUtc)
    {
        if (_runCharacterId is not { } characterId || _services.GetService<CqrsDispatcher>() is null)
            return;

        // Who commands the fleet decides whether this start becomes a shared run, so it is re-read here rather
        // than left to whichever tick last landed (ET-147).
        await RefreshFleetCommandAsync(DateTime.UtcNow);

        // Characters picked alongside this one (ET-210) need something to share, and StartRunCommandHandler only
        // mints one on its own for a fleet's commander — a manual multi-pick outside that case would otherwise
        // start N unrelated solo runs instead of one group of N.
        if (_additionalCharacters.Count > 0)
            GroupCode ??= RunGroupCode.Create();

        using var scope = _services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        // The whole group is on the one site, so its system is resolved once here rather than per character — a
        // sibling started in the same breath has no fleet-metric location sample of its own yet to resolve from.
        int? solarSystemId = _ResolveSolarSystemId();
        (string? fitContentHash, string? fitNameSnapshot) = await _ResolveFitAsync(characterId);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(characterId, Kind, startedAtUtc,
                // No type id: a signature names a dungeon, and the catalogue's DungeonId is not the type id this
                // column holds. The name travels instead.
                SiteTypeId: 0,
                SiteName: SignatureName,
                SolarSystemId: solarSystemId,
                GroupCode: GroupCode,
                Signature: SignatureId,
                FleetId: FleetId,
                // The one thing that turns a site start into a shared run. Not CanControl: a member steering their
                // own run may control it without commanding anybody, and answering "am I the boss" is the
                // authority's own job rather than something reassembled here (ET-147, ET-152).
                IsFleetCommander: Authority.IsFleetCommander,
                FitContentHash: fitContentHash,
                FitNameSnapshot: fitNameSnapshot,
                CharacterNameSnapshot: _runCharacterName,
                SignatureGroupSnapshot: SignatureGroup,
                SolarSystemName: SolarSystem,
                // This window's own start button is the clipboard/signature path — the site comes from what the
                // pilot pasted, not from a catalogue pick (ET-163).
                Origin: EveUtils.Shared.Modules.Runs.Enums.RunOrigin.Clipboard,
                // Every non-mission caller leaves AgentId/MissionLevel/Parameters at their defaults (null, null,
                // empty) — only ClipboardMissionOffer ever sets them (ET-172 sub 4).
                SiteTypeSource: _SiteTypeSource(),
                AgentId: MissionAgentId,
                MissionLevel: MissionLevel,
                Parameters: PendingParameters,
                // Announced to the fleet only if this window already knows them (ET-241) — SAVE is what actually
                // persists them onto the run, through ActivityWindowSectionViewModel.AddToSave.
                AbyssalTierIndex: TierIndex,
                AbyssalWeatherName: Weather?.Name));
        if (!started.IsSuccess)
        {
            _services.GetService<IToastService>()?.Show("Run not started",
                started.Messages.FirstOrDefault()?.Text ?? "Could not start this run.", ToastKind.Error);
            return;
        }

        RunId = started.Value;
        // The handler mints the group code when the window had none, and the command only ever gave the run id back
        // — so a commander's own window did not know the code of the run it had just started. With no code it fell
        // through RunControlAuthority's solo branch, and DISCARD, which only announces itself when it has one, never
        // reached a single other member (Raymond, 2026-09-03). Scoped to this pilot's own run (ET-130 deel 2): an
        // unscoped read here would trip over any OTHER character's run already going on a different site.
        if (GroupCode is null)
            GroupCode = (await dispatcher.Query(new GetRunningRunQuery(characterId))).Value?.GroupCode;

        // Every other character picked alongside this one (ET-210) gets its own row under the same GroupCode, filed
        // as though it started on its own — FleetRunGroupCodeCoordinator already treats N members starting on one
        // group code as ordinary, whether those members are on N machines or, as here, all local to this one.
        //
        // A pocket is a per-pilot space (ET-250): a picked toon has its own client and crosses in on its own moment,
        // same as a hauler-toon that never goes in at all must never start (the ET-246 "never went in" rule, applied
        // to this pilot's own toons). So for a run whose clock is per pilot nothing starts here — each one waits in
        // _ownLegsPending for _RefreshOwnPilotLegs to see it cross on its own account.
        if (_additionalCharacters.Count > 0)
        {
            if (RunType.ClockPerPilot)
                _ownLegsPending.AddRange(_additionalCharacters);
            else
                foreach ((int Id, string Name) extra in _additionalCharacters)
                    await _SendAdditionalStartRunCommandAsync(dispatcher, extra.Id, extra.Name, startedAtUtc, solarSystemId);
            _additionalCharacters = [];
        }

        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Start a run for a character riding along on this window's own start (ET-210) — same site, same
    /// group code, never the fleet commander (only the acting character ever is). Best-effort: one extra character
    /// failing to register is reported and does not undo the run this window itself already has.</summary>
    private async Task _SendAdditionalStartRunCommandAsync(
        CqrsDispatcher dispatcher, long characterId, string characterName, DateTime startedAtUtc, int? solarSystemId)
    {
        // Fit is genuinely per pilot — each toon flies its own ship — so unlike the site's system this is read
        // fresh for this specific character, not carried over from the one that started the group (ET-210 review
        // finding, 2026-09-25: a sibling's saved activity showed "fit not recognised" even though every toon's own
        // fit was visible and shared live, because nothing here ever asked for it).
        (string? fitContentHash, string? fitNameSnapshot) = await _ResolveFitAsync(checked((int)characterId));
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, Kind, startedAtUtc,
            SiteTypeId: 0,
            SiteName: SignatureName,
            SolarSystemId: solarSystemId,
            GroupCode: GroupCode,
            Signature: SignatureId,
            FleetId: FleetId,
            IsFleetCommander: false,
            FitContentHash: fitContentHash,
            FitNameSnapshot: fitNameSnapshot,
            CharacterNameSnapshot: characterName,
            SignatureGroupSnapshot: SignatureGroup,
            SolarSystemName: SolarSystem,
            Origin: EveUtils.Shared.Modules.Runs.Enums.RunOrigin.Clipboard,
            SiteTypeSource: _SiteTypeSource(),
            AgentId: MissionAgentId,
            MissionLevel: MissionLevel,
            Parameters: PendingParameters));

        if (!started.IsSuccess)
        {
            _services.GetService<IToastService>()?.Show("A character was not added to this run",
                started.Messages.FirstOrDefault()?.Text ?? "Could not start this run for one of the picked characters.",
                ToastKind.Error);
            return;
        }

        // Watched from the same instant its own run exists (ET-210 review, round 4) — this character's own gamelog
        // counts towards the group's enemies from here on, exactly like its bounty and loot already do.
        foreach (RunWindowSection section in _AllSections())
            section.OnCharacterRunStarted(checked((int)characterId));
    }

    /// <summary>The pilot's own solar system, resolved once for the whole group starting on it (ET-210 review
    /// finding, 2026-09-25): a site or an abyssal run only ever had the LOCATION section's own live name
    /// (<see cref="SolarSystem"/>) — never turned into the numeric id <c>Run.SolarSystemId</c> actually stores, so a
    /// saved activity's LOCATION always read "not recorded" regardless of how many characters it held. Null when the
    /// SDE has no exact match, same as an unmatched site name reads elsewhere in this window (ET-178).
    ///
    /// A mission with a "Report to" agent the SDE recognises prefers that agent's own station
    /// (<see cref="MissionSolarSystemId"/>) — unchanged since ET-176. Falls back to the pilot's own live system,
    /// exactly like a site does, for a regular agent's mission (no "Report to" line, ET-253): that capture never
    /// carries a system of its own, but the pilot's own location is known live the whole time the run window is
    /// open, and was simply never asked for.</summary>
    private int? _ResolveSolarSystemId() => MissionSolarSystemId ?? (SolarSystem is { Length: > 0 } name
        ? _services.GetService<ISdeAccessor>()?.FindSolarSystemByName(name)?.SolarSystemId
        : null);

    /// <summary>Which id space the stored run's site came from. A kind check kept on purpose (ET-236): this is how
    /// the store files the row's site, keyed on the kind it is filed under, not anything a section shows. A site with
    /// a copied name the catalogue never carries is Uncatalogued, not Site with a blank id (ET-178 AC-2): otherwise it
    /// reads back no differently from a site that was never named at all.</summary>
    private SiteTypeSource _SiteTypeSource() => Kind switch
    {
        ActivityKind.Mission => SiteTypeSource.Mission,
        ActivityKind.Site when SignatureName is not null && MatchedSites.Count == 0 => SiteTypeSource.Uncatalogued,
        _ => SiteTypeSource.Site
    };

    /// <summary>What ET-101's own detection already knows for this specific character, turned into what
    /// <c>StartRunCommand</c> stores — <see cref="IShipFitDetectionService"/> answers per character, not only for
    /// the acting one, so a sibling's own fit is exactly as reachable as the acting character's always was; nothing
    /// here previously asked it the question at all.</summary>
    private async Task<(string? ContentHash, string? NameSnapshot)> _ResolveFitAsync(int characterId)
    {
        if (_services.GetService<IShipFitDetectionService>()?.GetReading(characterId).SelectedFit is not { } selected)
            return (null, null);

        string? contentHash = _services.GetService<IFittingRepository>() is { } fittings
            ? (await fittings.FindByIdAsync(selected.Id))?.ContentHash
            : null;
        return (contentHash, selected.Name);
    }

    /// <summary>Stop the clock. The stored run stays open until SAVE or DISCARD: loot is copied out of the wreck
    /// after the last rat, and it belongs to the run that produced it. The enemy list stays for the same reason and
    /// more so — its count is typed by hand, and nobody types it while still being shot at (ET-115).</summary>
    public void StopRun(DateTime nowUtc)
    {
        if (RunState != ActivityRunState.Running)
            return;

        StoppedAtUtc = nowUtc;
        RunState = ActivityRunState.Stopped;
        CorrectedStartUtc = null;
        CorrectedStopUtc = null;
        TimeCorrectionError = null;
        // Seeded with what was measured, so correcting a start by half a minute is an edit and not a retype.
        StartCorrectionText = AnchorUtc is { } start ? _LocalTime(start) : string.Empty;
        EndCorrectionText = _LocalTime(nowUtc);
        _ = _SetStoredRunStoppedAsync(nowUtc);
        _AnnounceStopToFleet(nowUtc);
        Refresh(nowUtc);
    }

    /// <summary>
    /// Put the clock's rest — or its restart, with <paramref name="stoppedAtUtc"/> null — into the row itself.
    ///
    /// Until this call existed a stop lived on this view model alone: the row stayed Running for the rest of the
    /// session, so every window that opened afterwards adopted it through <c>_AdoptRunningRunAsync</c>, start time,
    /// site and commander's group code included. That is the whole of what the operator reported ten times, and it
    /// is also why a commander's STOP did not reach him — his window was on yesterday's group code, so the announced
    /// one never matched (measured, 2026-09-03).
    /// </summary>
    private async Task _SetStoredRunStoppedAsync(DateTime? stoppedAtUtc)
    {
        if (RunId is not { } runId || _services.GetService<CqrsDispatcher>() is null)
            return;

        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        await dispatcher.Send(new SetRunStoppedCommand(runId, stoppedAtUtc));
        // A pocket is a per-pilot space (ET-243, ET-250): every own toon riding along times its own leg off its own
        // crossing (_RefreshOwnPilotLegs), so this pilot's own STOP or resume moves nothing but this pilot's own row.
        if (RunType.ClockPerPilot)
            return;

        // A shared GroupCode here is always this pilot's own other toons (ET-210), never a remote fleet member's —
        // their row lives in a database this client cannot reach — so the clock this window's pilot controls is the
        // group's, and stopping every participant alongside this window's own run is never somebody else's to touch.
        // Unlike ET-105, where each member's clock is their own and only THAT member's STOP moves it.
        foreach (RunParticipantViewModel sibling in Participants.Where(participant => participant.RunId != runId))
            await dispatcher.Send(new SetRunStoppedCommand(sibling.RunId, stoppedAtUtc));
    }

    /// <summary>
    /// Bring every member's clock to rest at the commander's moment. There was no such announcement at all until
    /// now — START and DISCARD crossed the wire and STOP simply did not exist — so a member watched a run that had
    /// been over for minutes carry on counting (Raymond, 2026-09-03).
    ///
    /// Only from the window that commands the run: a member stopping their own leg ends nobody else's, and the
    /// event is received back here too, where <see cref="StopRun"/>'s own guard makes the second one a no-op.
    ///
    /// In a run whose clock is per pilot there is no such thing (ET-243): every pilot's stop — the commander's too —
    /// goes out as that pilot's own way out, which stops nobody and tells the shared clock who is still in. Only this
    /// window's own pilot: every other own toon riding along stops on its own way out instead (ET-250,
    /// <see cref="_RefreshOwnPilotLegs"/>), never in lockstep with this one — a hauler-toon still in the pocket must
    /// not be read as "out" because the acting character's own STOP was pressed.
    /// </summary>
    private void _AnnounceStopToFleet(DateTime stoppedAtUtc)
    {
        if (RunType.ClockPerPilot)
        {
            if (_runCharacterId is { } own)
                _AnnouncePilotStopToFleet(own, stoppedAtUtc);
            return;
        }

        if (FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _services.GetService<IEventBus>() is not { } eventBus || !Authority.CanControl)
            return;

        _ = eventBus.PublishAsync(
            new FleetRunStoppedEvent(new RunGroupStop(fleetId, Kind, groupCode, stoppedAtUtc)),
            EventTarget.Both);
    }

    /// <summary>One own character's own way out of a run whose clock is per pilot (ET-243, ET-250) — this window's own
    /// pilot from <see cref="_AnnounceStopToFleet"/>, or another own toon riding along from
    /// <see cref="_RefreshOwnPilotLegs"/>. A no-op outside a real fleet: nothing announces to nobody, and the stored
    /// row already moved regardless (<see cref="_SetStoredRunStoppedAsync"/>, <see cref="_StopOwnPilotLegAsync"/>).</summary>
    private void _AnnouncePilotStopToFleet(int characterId, DateTime stoppedAtUtc)
    {
        if (FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _ = eventBus.PublishAsync(
            new FleetRunPilotStoppedEvent(new RunGroupStop(fleetId, Kind, groupCode, stoppedAtUtc), characterId),
            EventTarget.Both);
    }

    /// <summary>One own character's own leg picked back up (ET-250) — this window's own pilot from
    /// <see cref="StartRunAsync"/>, or another own toon riding along from <see cref="_RefreshOwnPilotLegs"/>.</summary>
    private void _AnnouncePilotResumeToFleet(int characterId, DateTime startedAtUtc)
    {
        if (FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _ = eventBus.PublishAsync(
            new FleetRunPilotResumedEvent(new RunGroupResume(fleetId, Kind, groupCode, startedAtUtc), characterId),
            EventTarget.Both);
    }

    /// <summary>
    /// Take the two typed times as this run's own. Refused rather than straightened out when they cannot be true:
    /// an end before its start is not a duration, and an abyssal run longer than <c>AbyssalSpace.RunLimit</c> is a
    /// run whose pilot was dead before it ended.
    ///
    /// It moves this run's row and nothing else. The activity's own span is worked out from every pilot's saved run
    /// (first in to last out) — correcting your own clock does not re-anchor anybody, which is why
    /// <see cref="AnchorUtc"/> is left standing as the measured moment.
    /// </summary>
    [RelayCommand]
    private void ApplyTimeCorrection()
    {
        if (RunState != ActivityRunState.Stopped || AnchorUtc is not { } measuredStart)
            return;

        DateTime measuredStop = StoppedAtUtc ?? measuredStart;
        if (_ParseLocalTime(StartCorrectionText, measuredStart) is not { } start
            || _ParseLocalTime(EndCorrectionText, measuredStop) is not { } end)
        {
            TimeCorrectionError = "Both times are read as HH:mm:ss on the day the run was flown.";
            return;
        }

        if (end < start)
        {
            TimeCorrectionError = "The end cannot be before the start.";
            return;
        }

        if (_IsInPocket && end - start > AbyssalSpace.RunLimit)
        {
            TimeCorrectionError =
                $"An abyssal run cannot last longer than {AbyssalSpace.RunLimit.TotalMinutes:N0} minutes — "
                + "past that the ship and the pod are gone.";
            return;
        }

        TimeCorrectionError = null;
        // Only a time that differs is a correction; retyping what was measured leaves the run measured.
        CorrectedStartUtc = start == measuredStart ? null : start;
        CorrectedStopUtc = end == measuredStop ? null : end;
        Refresh(DateTime.UtcNow);
    }

    /// <summary>The start SAVE writes and the clock counts from: the correction when there is one, the measured
    /// moment otherwise.</summary>
    public DateTime? EffectiveStartUtc => CorrectedStartUtc ?? AnchorUtc;

    public DateTime? EffectiveStopUtc => CorrectedStopUtc ?? StoppedAtUtc;

    /// <summary>HH:mm:ss on the day of <paramref name="referenceUtc"/>, in the pilot's own zone — the same shape
    /// the figure above the box is printed in. The reference is per field, so a run over local midnight keeps its
    /// end on the day it ended.</summary>
    private static DateTime? _ParseLocalTime(string? text, DateTime referenceUtc) =>
        TimeSpan.TryParseExact(text?.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out TimeSpan time)
            ? DateTime.SpecifyKind(referenceUtc.ToLocalTime().Date + time, DateTimeKind.Local).ToUniversalTime()
            : null;

    /// <summary>
    /// Re-test who may steer this run against the fleet boss ESI reports right now. Called whenever the roster or
    /// the boss changes, not once at start: a handover mid-run moves the controls to the new FC and takes them off
    /// the old one, and that is an ordinary state change (ET-105). <paramref name="fleetCommanderCharacterId"/> null
    /// means the roster could not say — the controls go away and say why, rather than appearing for everybody.
    /// Whether the run is shared at all is the run's own <see cref="GroupCode"/>, not <paramref name="fleetId"/>:
    /// this client having no ET fleet active is not the same thing as flying alone (ET-135).
    /// </summary>
    public void ApplyFleetCommand(long? fleetId, int? fleetCommanderCharacterId, int? actingCharacterId,
        string? fleetCommanderName = null)
    {
        FleetId = fleetId;
        Authority = RunControlAuthority.From(
            fleetId, fleetCommanderCharacterId, actingCharacterId, GroupCode, fleetCommanderName);
    }

    /// <summary>
    /// Where the two halves of that question come from, and both now out of the same membership set. The fleet is
    /// the one this client is participating in — what the run is filed under and what a discard fans out over — and
    /// the commander is whoever holds that role on its ET roster, null when the roster could not be read.
    ///
    /// It used to ask ESI for the in-game fleet boss, which only answers for a coupled fleet: an ordinary ET fleet
    /// never produced one, so its commander was told his own controls were hidden because nobody knew who he was
    /// (ET-152). The command answer is applied before the unstarted-fleet lookup, so controls settle when the window opens.
    /// </summary>
    public async Task RefreshFleetCommandAsync(DateTime nowUtc)
    {
        // A run filed under a group belongs to the fleet whose commander made that group, and a membership sweep
        // that has not answered yet must not take it away again: on a joining member the announcement arrives
        // before his own participation does, and the first tick erased the fleet id it had just been handed.
        List<long> inPlay = _MyFleetIds();
        long? fleetId = _ActingFleetId(inPlay) ?? (GroupCode is not null ? FleetId : null);
        // Counted off the same list the pick was made on, so the notice and the outcome are one reading of the set
        // rather than two that a sweep in between could have pulled apart.
        FleetsInPlay = inPlay.Count;
        // The same character the rest of the window works from. It used to fall back to IActiveFleetState, which
        // holds whichever character a fleets-window row was last selected as — so a fleet commander flying a
        // different toon than that row's acting one was compared against his own fleet's boss id and told only the
        // FC may start or stop (Jithran, 2026-09-02). The boss is looked up for this same character, so both sides
        // of the comparison are now one pilot.
        int? commander = _CommanderOf(fleetId);
        ApplyFleetCommand(fleetId, commander, _ActingCharacterId(), _CommanderNameOf(commander));
        // Right after the verdict it hangs on: who commands the fleet is the one thing it needs this sweep to settle.
        _OfferPreparedRunToFleet();
        if (FleetsInPlay > 0)
        {
            UnstartedFleetName = null;
            FormingFleetCount = 0;
            _unstartedFleetNoticeCheckedAtUtc = null;
        }
        // A text hint can wait briefly: checking every clock tick wastes work, but checking only once hides new fleets.
        else if (_runCharacterId is not null && (_unstartedFleetNoticeCheckedAtUtc is null
                                                || nowUtc - _unstartedFleetNoticeCheckedAtUtc >= UnstartedFleetNoticeRefreshInterval))
        {
            _unstartedFleetNoticeCheckedAtUtc = nowUtc;
            (UnstartedFleetName, FormingFleetCount) = await _UnstartedFleetNameAsync();
        }
    }

    /// <summary>
    /// Put this pocket out to the fleet before anybody is in it (ET-246): this pilot commands the fleet, the window is
    /// armed on nothing yet, and the tier and weather are known — what a member needs to pick the same filament. The
    /// group code is minted here, ahead of the run, so the start that follows files every pilot's run under the code
    /// they were offered. Once per window; a later change of tier or weather reaches the members the way it always
    /// did (ET-241), because the run now has a code to send it under.
    /// </summary>
    private void _OfferPreparedRunToFleet()
    {
        if (_hasPreparedOffer || !RunType.ClockPerPilot || RunState is not ActivityRunState.NotStarted
            || RunId is not null || GroupCode is not null || !Authority.IsFleetCommander || FleetId is not { } fleetId
            || !HasWeatherAndTier || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        // Before the code, not after: setting it re-runs this sweep, which must find the offer already made.
        _hasPreparedOffer = true;
        string groupCode = RunGroupCode.Create();
        GroupCode = groupCode;
        _ = eventBus.PublishAsync(new FleetRunGroupPreparedEvent(
            new RunGroupCodeStart(fleetId, Kind, groupCode, DateTime.UtcNow, IsFleetCommander: true,
                SolarSystemName: SolarSystem, SignatureGroupSnapshot: SignatureGroup,
                AbyssalTierIndex: TierIndex, AbyssalWeatherName: Weather?.Name),
            _ActingCharacterId()), EventTarget.Both);
        _RefreshArmed();
    }

    /// <summary>
    /// The commander closes a prepared run that nobody went into (ET-246): the offer comes down on every member's screen
    /// and nothing is left behind. It is the same <c>fleet.run-discarded</c> an ended run sends — there is no row
    /// anywhere for it to end. Once any pilot is in, closing this window is only this pilot sitting it out.
    /// </summary>
    private void _WithdrawPreparedOffer()
    {
        if (!_hasPreparedOffer || RunState is not ActivityRunState.NotStarted
            || FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _fleetLegs?.Of(groupCode) is { Count: > 0 } || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _hasPreparedOffer = false;
        _isDiscarding = true;
        _ = eventBus.PublishAsync(
            new FleetRunDiscardedEvent(new RunGroupDiscard(fleetId, Kind, groupCode, DateTime.UtcNow)),
            EventTarget.Both);
    }

    /// <summary>
    /// The forming fleets for this run's character, from the same repository call this sweep already made — no
    /// second call, just a different read of what came back (ET-201 AC-5). One forming fleet is
    /// named; more than one is only ever counted, never narrowed to "the most likely one" by recency or headcount —
    /// that guess is what PR #223 tried to ban and ET-201 exists to finish (AC-1/AC-2).
    /// </summary>
    private async Task<(string? Name, int FormingCount)> _UnstartedFleetNameAsync()
    {
        if (_runCharacterId is not { } characterId)
            return (null, 0);

        using IServiceScope scope = _services.CreateScope();
        IReadOnlyList<FleetEntity> fleets = await scope.ServiceProvider.GetRequiredService<IFleetRepository>()
            .ListForParticipantAsync(characterId);
        List<FleetEntity> forming = fleets.Where(fleet => fleet.Activation == FleetActivation.Forming).ToList();
        return forming switch
        {
            [{ } only] => (only.Name, 1),
            { Count: > 1 } several => (null, several.Count),
            _ => (null, 0)
        };
    }

    /// <summary>
    /// What to call the commander, so the run controls can say who instead of printing his character id at a pilot
    /// (Raymond, 2026-09-03). Resolved here rather than in <see cref="RunControlAuthority"/>: that record decides
    /// who may press what and holds ids, and giving it a name lookup would give the shared layer a dependency the
    /// names already sit above.
    ///
    /// One lookup per commander, not one per clock tick: the id is claimed before the lookup runs, so an FC who
    /// cannot be named is asked about once and the sentence goes out without a name.
    /// </summary>
    private string? _CommanderNameOf(int? commanderCharacterId)
    {
        if (commanderCharacterId is not { } characterId)
            return null;

        if (_commanderNameId != characterId)
        {
            _commanderNameId = characterId;
            _commanderName = null;
            _ = _ResolveCommanderNameAsync(characterId);
        }

        return _commanderName;
    }

    private async Task _ResolveCommanderNameAsync(int characterId)
    {
        string? name = await _NameOfAsync(characterId);
        if (_commanderNameId == characterId)
            _commanderName = name;
    }

    /// <summary>Who commands <paramref name="fleetId"/> on its ET roster, as the last membership sweep read it.
    /// Null when there is no fleet or the sweep could not say.</summary>
    private int? _CommanderOf(long? fleetId) =>
        fleetId is { } id
            ? _Participation().FirstOrDefault(participant => participant.FleetId == id).FleetCommanderCharacterId
            : null;

    /// <summary>Guards <see cref="_RefreshParticipantsAsync"/> against overlapping ticks: a slow query outliving one
    /// second would otherwise let two calls both pass the "not there yet" check on the same row and add it twice.</summary>
    private bool _isRefreshingParticipants;

    /// <summary>
    /// Who has a run in this activity, on the definition <c>ActivitySummary.ParticipantCount</c> already uses
    /// (ET-131): the runs sharing this <see cref="GroupCode"/>, or just this run when it is flown alone. Rows are
    /// kept and updated rather than rebuilt, the same as <see cref="_SyncFleetMembers"/>, so a payout toggle from
    /// <see cref="SetPayoutEligibilityAsync"/> is not raced by the next tick's read of the same row.
    /// </summary>
    private async Task _RefreshParticipantsAsync()
    {
        if (RunId is not { } runId || _services.GetService<CqrsDispatcher>() is null || _isRefreshingParticipants)
            return;

        _isRefreshingParticipants = true;
        try
        {
            using var scope = _services.CreateScope();
            Result<IReadOnlyList<RunGroupParticipantDto>> result = await scope.ServiceProvider
                .GetRequiredService<CqrsDispatcher>().Query(new GetRunGroupParticipantsQuery(GroupCode, runId));
            if (!result.IsSuccess || result.Value is not { } participants)
                return;

            foreach (RunGroupParticipantDto dto in participants)
            {
                if (Participants.FirstOrDefault(row => row.RunId == dto.RunId) is { } existing)
                {
                    existing.IsParticipant = dto.IsParticipant;
                    existing.IsPayoutEligible = dto.IsPayoutEligible;
                    existing.BountyIsk = dto.BountyIsk;
                    continue;
                }

                // Never actually wider than int32: every other table in this schema stores an EVE character id as
                // int (EsiCharacterId, FleetParticipant, gamelog's CharacterId, ...); Run.CharacterId is long only
                // because Run predates that convention, not because a real id needs the extra room.
                int characterId = checked((int)dto.CharacterId);
                string name = await _NameOfAsync(characterId) ?? $"Char {characterId}";
                Participants.Add(new RunParticipantViewModel(
                    dto.RunId, characterId, name, dto.IsParticipant, dto.IsPayoutEligible, dto.BountyIsk));
            }

            foreach (RunParticipantViewModel gone in Participants
                         .Where(row => participants.All(dto => dto.RunId != row.RunId)).ToList())
                Participants.Remove(gone);
            _SyncLootOverview();

            OnPropertyChanged(nameof(IsFleetShown));
            // A group with more than one participant is what turns the header chip from one name into the group's
            // (ET-130 deel 3) — this is the only place Participants changes outside the constructor, so it is the
            // only place that has to say so.
            OnPropertyChanged(nameof(ActingCharacterText));
            RecomputePayout();
        }
        finally
        {
            _isRefreshingParticipants = false;
        }
    }

    /// <summary>
    /// Take a character out of the ISK split, or put them back in. Never touches their participation: they flew the
    /// site either way and their loot stays recorded (ET-105 AC-3).
    /// </summary>
    public async Task<bool> SetPayoutEligibilityAsync(RunParticipantViewModel participant, bool isPayoutEligible)
    {
        using var scope = _services.CreateScope();
        Result result = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Send(new SetRunPayoutEligibilityCommand(participant.RunId, isPayoutEligible));
        if (!result.IsSuccess)
            return false;

        participant.IsPayoutEligible = isPayoutEligible;
        RecomputePayout();
        return true;
    }

    /// <summary>Redivide the expected ISK over whoever still takes a share.</summary>
    public void RecomputePayout() => RunPayoutSplit.Apply([.. Participants], TotalLootIsk);

    /// <summary>What there is to divide, as far as the loot section knows. Null while nothing is priced — never 0,
    /// which would read as "there was nothing" (ET-65 AC-5's rule).</summary>
    [ObservableProperty] private decimal? _totalLootIsk;

    partial void OnTotalLootIskChanged(decimal? value) => RecomputePayout();

    /// <summary>Set for the whole of <see cref="SaveRunAsync"/> — saving a group of five runs used to look like
    /// nothing was happening for five to six seconds (ET-210 review finding, 2026-09-09), because each of the five
    /// SAVEs ran its own full activity-summary rebuild in sequence. Bound to disable SAVE and say so, the same
    /// pattern <c>SettingsBackupsViewModel.IsBusy</c> already uses for a copy that takes long enough to look stuck.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isSaving;

    public string SaveButtonText => IsSaving ? "SAVING…" : "SAVE";

    private RunSaveDraft _SaveDraftFor(Guid runId, int? characterId, bool isActingRun)
    {
        var draft = new RunSaveDraft(runId, characterId, isActingRun);
        foreach (RunWindowSection section in _AllSections())
            section.AddToSave(draft);
        return draft;
    }

    /// <summary>
    /// This member commits their own part of the run — every member's own button, never the FC's (ET-105). The
    /// enemy observations are converted here: ET-106 left that seam open so the run would have one lifecycle
    /// rather than two.
    /// </summary>
    [RelayCommand]
    private async Task SaveRunAsync()
    {
        if (RunId is not { } runId)
        {
            RunNoticeText = "This run was never registered, so there is nothing to save it to.";
            _services.GetService<IToastService>()?.Show("Run not saved", RunNoticeText, ToastKind.Error);
            return;
        }

        IsSaving = true;
        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            using var scope = _services.CreateScope();
            CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();

            // Bounty is no longer collected here at all (ET-219): GamelogClientService.AddBountyAsync writes every
            // payout straight onto its own run's RunBountyEntry rows as it comes in, for every character — acting or
            // sibling — independently of which one this window's column happens to be showing. SAVE sending its own
            // bounty lines on top of that would double it, so this call (and the sibling one below) always sends
            // none of its own.
            //
            // The summary rebuild is deferred to one call after every row in the group is saved (ET-210 review
            // finding): it scans every saved run in the store and prices its loot, and running that scan once per
            // row — five times for a five-character group — was the whole of the five-to-six-second stall.
            //
            // What each run carries besides its times — its enemies, its escalation, how it was looted — is the
            // sections' to add (ET-236).
            RunSaveDraft own = _SaveDraftFor(runId, _runCharacterId, isActingRun: true);
            Result result = await dispatcher.Send(new SaveRunCommand(
                runId, EffectiveStopUtc ?? nowUtc, nowUtc, [], [],
                own.Enemies,
                own.Parameters,
                // Null leaves the row's own start alone; only a hand-corrected start travels.
                CorrectedStartUtc,
                IsTimeCorrected ? nowUtc : null,
                LootStrategy: own.LootStrategy,
                RebuildSummaries: false));
            if (!result.IsSuccess)
            {
                RunNoticeText = result.Messages.FirstOrDefault()?.Text ?? "Could not save this run.";
                _services.GetService<IToastService>()?.Show("Run not saved", RunNoticeText, ToastKind.Error);
                return;
            }

            RunNoticeText = null;
            RunState = ActivityRunState.Saved;
            // Toons of the same pilot save together (ET-210): STOP and SAVE apply to the whole group, unlike ET-105
            // where each fleet member commits their own part on their own machine — Participants here is always this
            // pilot's own other local runs (a remote member's row is never in this database), never somebody else's
            // to commit.
            foreach (RunParticipantViewModel sibling in Participants.Where(participant => participant.RunId != runId))
            {
                RunSaveDraft theirs = _SaveDraftFor(sibling.RunId, sibling.CharacterId, isActingRun: false);
                Result siblingResult = await dispatcher.Send(new SaveRunCommand(
                    sibling.RunId, EffectiveStopUtc ?? nowUtc, nowUtc, [], [],
                    theirs.Enemies, theirs.Parameters,
                    LootStrategy: theirs.LootStrategy, RebuildSummaries: false));
                if (!siblingResult.IsSuccess)
                    _services.GetService<IToastService>()?.Show("A run in this group was not saved",
                        siblingResult.Messages.FirstOrDefault()?.Text ?? "Could not save one of the other characters' runs.",
                        ToastKind.Error);
            }

            // The one rebuild the whole group's saves needed, run once now that every row is in.
            await dispatcher.Send(new RebuildActivitySummariesCommand());
            _OnRunClosed();
            if (RunLoot is not null)
                await RunLoot.RefreshAsync();
            Refresh(nowUtc);
            // Only here, and only for this window: the run is committed and there is nothing left to do to it. A
            // failed save falls out above with the reason still on screen, and a group's other members keep their
            // own windows — saving is each member's own, and only the FC's DISCARD reaches anybody else (ET-105).
            // Saving is one of the two answers to a waiting copy, so it hands it on the same way DISCARD does; until
            // 2026-09-04 the copy simply went with the window.
            _SendPendingCopyToANewWindow();
            CloseRequested?.Invoke();
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Raised when this window is done with its run and should go away: a save that landed, or a discard by
    /// the pilot who commands the run (ET-155). The window closes on it; nothing else listens, and nothing crosses to
    /// another member's window — a member whose commander discarded keeps his window and closes it himself.</summary>
    public event Action? CloseRequested;

    /// <summary>The one line on this window that stays put: why the last save did not land, or — since ET-155 — that
    /// the commander threw the shared run away. A toast is gone in seconds, and both of these are states a pilot may
    /// only look at minutes later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunNotice))]
    private string? _runNoticeText;

    public bool HasRunNotice => RunNoticeText is not null;

    /// <summary>
    /// Throw this pilot's own run(s) away, and end the shared run for everyone else in it. Confirmed first, because
    /// it reaches every other member's machine — and it still takes nothing from them: a member who already saved
    /// keeps their run, merely unlinked from the group (ET-105 AC-1). What DISCARD takes from this pilot is
    /// different: their own run here — an ET-210 group of their own toons included — is not yet saved, and pressing
    /// this button means they never want it back, so it is soft-deleted rather than left to sit in UNFINISHED
    /// (ET-220).
    /// </summary>
    [RelayCommand]
    private async Task DiscardRunAsync()
    {
        if (!Authority.CanControl || RunId is not { } runId)
            return;

        var dialogs = _services.GetRequiredService<IDialogService>();
        // Shared-ness is GroupCode and nothing else (ET-152) — the same test the rest of this window uses, so the
        // question this dialog asks matches what DISCARD actually does below: a solo run reaches nobody but this
        // window, and saying "the fleet" over one is the wrong warning read at the moment it matters most. A real
        // fleet (FleetId set) is the only case that reaches someone else at all — GroupCode alone also covers
        // ET-210's own multi-toon pick, which is nobody's business but this pilot's own.
        string discardMessage = FleetId is not null && GroupCode is not null
            ? "This ends the run for every member of the fleet, and throws your own copy of it away. Nobody else "
              + "loses what they already saved — their run stays, on its own, no longer part of this group."
            : GroupCode is not null
                ? "This throws away every one of your own runs in this group. None of them will be saved, and none "
                  + "will show up as unfinished."
                : "This throws the run away. It won't be saved, and it won't show up as unfinished.";
        if (!await dialogs.ConfirmAsync("Discard this run?", discardMessage, "Discard"))
            return;

        DateTime nowUtc = DateTime.UtcNow;
        // Captured before the group ends below (GroupCode = null): this is what tells the Undo toast, and a later
        // undo, whether to restore one run or the whole group. Null whenever a fleet is involved — a real fleet's
        // group discard is the FC's shared decision, not this pilot's own thing to undo alone.
        string? ownGroupCode = FleetId is null ? GroupCode : null;
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        // A fleet's own group code is discarded whole already, below: FleetRunDiscardedEvent (EventTarget.Both)
        // reaches FleetRunGroupCodeCoordinator on THIS client too, and it already runs DiscardRunsInGroupCommand for
        // every local row sharing the code — a second one here would be redundant, not wrong, but there is no reason
        // to race it. Outside a fleet (ET-210's manual multi-pick, which mints its own group code with no fleet to
        // announce to) nothing else ever discards the siblings, so this is the only place it happens. Either way,
        // DeleteAfterDiscard: true — this pilot is discarding their own run(s) by their own hand, not receiving
        // someone else's fanout, which is the one case (FleetRunGroupCodeCoordinator's own call, further down) that
        // must keep it false.
        Result discarded = ownGroupCode is { } soloGroupCode
            ? await dispatcher.Send(new DiscardRunsInGroupCommand(soloGroupCode, nowUtc, DeleteAfterDiscard: true))
            : await dispatcher.Send(new DiscardRunCommand(runId, nowUtc, DeleteAfterDiscard: true));
        if (!discarded.IsSuccess)
        {
            _services.GetService<IToastService>()?.Show("Run not discarded",
                discarded.Messages.FirstOrDefault()?.Text ?? "Could not discard this run.", ToastKind.Error);
            return;
        }

        _isDiscarding = true;
        if (FleetId is { } fleetId && GroupCode is { } groupCode)
            await _services.GetRequiredService<IEventBus>().PublishAsync(
                new FleetRunDiscardedEvent(new RunGroupDiscard(fleetId, Kind, groupCode, nowUtc)),
                EventTarget.Both);

        // Soft-deleted, not gone for good (ET-220): a ten-minute run is a painful thing to lose to a misclick, and
        // Undo here answers that exactly like ET-214's own delete does — RestoreRunCommand/RestoreRunsInGroupCommand,
        // the same pair, un-deleting back to a Stopped, not-yet-saved run that lands right back in UNFINISHED for
        // this pilot to decide about again.
        _services.GetService<IToastService>()?.Show("Run thrown away",
            ownGroupCode is not null
                ? "Every one of your own runs in this group is gone. Undo brings the whole group back."
                : "This run is gone. Undo brings it back, unfinished, exactly where it left off.",
            ToastKind.Success,
            [new ToastAction("Undo", () => _ = _UndoDiscardAsync(ownGroupCode, runId))]);

        // Thrown away means this window is done, so it closes (ET-155). It used to be cleaned out and left standing
        // ready for the next START, which is the very shape in which old run state kept coming back. Only here: a
        // refused command and a cancelled confirmation both fall out above with the window still on its run.
        _SendPendingCopyToANewWindow();
        GroupCode = null;   // the group ended with the run, which is what a discard reaches the other members to say.
        CloseRequested?.Invoke();
    }

    /// <summary>The Undo toast's own callback (ET-220) — a fresh scope of its own, since the toast can outlive the
    /// scope <see cref="DiscardRunAsync"/> disposed when it returned.</summary>
    private async Task _UndoDiscardAsync(string? groupCode, Guid runId)
    {
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        Result restored = groupCode is { } code
            ? await dispatcher.Send(new RestoreRunsInGroupCommand(code))
            : await dispatcher.Send(new RestoreRunCommand(runId));
        if (!restored.IsSuccess)
            _services.GetService<IToastService>()?.Show("Run not restored",
                restored.Messages.FirstOrDefault()?.Text ?? "Could not undo the discard.", ToastKind.Error);
    }

    /// <summary>
    /// Where the site copied during the run ends up now that the window closes instead of clearing itself. Not a new
    /// route: a signature copied with no run window open opens a fresh window on it, and that is exactly what this
    /// hands to <see cref="IDialogService.ShowActivityWindow"/> — so the copy is answered the way every other copy
    /// is, rather than evaporating with the window that was holding it (ET-155).
    /// </summary>
    private void _SendPendingCopyToANewWindow()
    {
        if (_pendingCopy is not { } pending || _services.GetService<IDialogService>() is not { } dialogs)
            return;

        _pendingCopy = null;
        dialogs.ShowActivityWindow(new ActivityWindowViewModel(pending.Kind, _services)
        {
            SignatureId = pending.SignatureId,
            SignatureGroup = pending.SignatureGroup,
            SignatureName = pending.Name,
            MatchedSites = pending.Sites,
            MissionAgentId = pending.AgentId,
            MissionLevel = pending.MissionLevel,
            MissionSolarSystemId = pending.SolarSystemId,
            PendingParameters = pending.Parameters,
            StartsOnArrival = pending.StartsOnArrival
        });
    }

    /// <summary>A copy waiting behind the run on screen — a site, or a mission with its agent. Plain data rather than
    /// a built view model: that constructor subscribes to the gamelog and five event-bus topics, so a parked one
    /// would answer them.</summary>
    private sealed record PendingCopy(ActivityKind Kind, string Name, bool StartsOnArrival, string? SignatureId,
        string? SignatureGroup, IReadOnlyList<SdeSite> Sites, int? AgentId, int? MissionLevel, int? SolarSystemId,
        IReadOnlyList<RunParameterInput> Parameters);

    /// <summary>
    /// Answer the close on a run that is not saved yet. The question lives here because a run outlives its window in
    /// the store: a close that decides nothing left the row open, and the next window adopted it — start time, site
    /// and the commander's group code included (Raymond, ten reports, 2026-09-03).
    ///
    /// A running clock is brought to rest first, so the question is about a finished stretch rather than a moving
    /// one. That costs nothing: STOP is a pause, and SAVE writes <see cref="EffectiveStopUtc"/> either way.
    ///
    /// Never gated on <see cref="RunControlAuthority.CanControl"/>. A member flying the commander's run may not end
    /// it for the fleet, but the row this window made for him is his own, and refusing him the answer would leave
    /// him unable to close without keeping exactly the state this whole question exists to clear.
    /// </summary>
    public async Task<bool> RequestCloseAsync()
    {
        _WithdrawPreparedOffer();
        if (RunId is not { } runId || RunState is ActivityRunState.NotStarted or ActivityRunState.Saved
            || _services.GetService<CqrsDispatcher>() is null)
            return true;

        if (RunState is ActivityRunState.Running)
            StopRun(DateTime.UtcNow);

        bool? save = await _services.GetRequiredService<IDialogService>().ChooseAsync("Close this run?",
            "This run is not saved yet. Save it, or throw your own registration away — either way the run ends here.",
            "Save", "Discard");
        if (save is null)
            return false;

        if (save.Value)
        {
            await SaveRunCommand.ExecuteAsync(null);
            return RunState is ActivityRunState.Saved;   // a refused save keeps the window, with the reason on it
        }

        // This pilot's own row and nothing else. Announcing the end to the fleet hangs on CanControl and lives in
        // DiscardRunAsync; throwing away your own registration is not that, so no FleetRunDiscardedEvent goes out.
        // DeleteAfterDiscard: true for the same reason as DiscardRunAsync (ET-220) — "throw your own registration
        // away" above already says this is a delete, not a mere stop.
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Send(new DiscardRunCommand(runId, DateTime.UtcNow, DeleteAfterDiscard: true));
        // Same Undo toast as DiscardRunAsync (ET-220) — a misclick on "Discard" here is just as painful as one on
        // the DISCARD button, and the window is already on its way out either way.
        _services.GetService<IToastService>()?.Show("Run thrown away",
            "This run is gone. Undo brings it back, unfinished, exactly where it left off.", ToastKind.Success,
            [new ToastAction("Undo", () => _ = _UndoDiscardAsync(groupCode: null, runId))]);
        return true;
    }

    /// <summary>
    /// A clipboard copy has just been filed against a run. The LOOT section itself is only redrawn when it is
    /// <i>this</i> window's own run, so a second window on another run does not redraw for loot that is not its
    /// own — the window reads its loot from the store, and until this arrived nothing told it to read again: a copy
    /// taken while the window stood open was stored, toasted as "Loot copied", and left the LOOT section under it
    /// still reading "no loot captured" (Raymond, 2026-09-02). The run had the loot; the window simply never looked.
    ///
    /// The group total's own per-run cache (ET-211) is kept for EVERY participant's run, own or sibling: with loot
    /// now attributed to whichever character actually copied it, a sibling's run can hold captures this window's
    /// LOOT section never shows and the group total still has to count.
    /// </summary>
    private void _OnRunLootCaptured(RunLootCapturedEvent integrationEvent)
    {
        Guid capturedRunId = integrationEvent.Data;
        if (RunLoot is not null && capturedRunId == RunId)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RunLoot.RefreshAsync());

        if (LootOverview is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LootOverview.RefreshRunAsync(capturedRunId));
    }

    /// <summary>
    /// One block per run in the group, the character column's own choice aside (ET-215): the participants once the
    /// store has named them, and until then this window's own run on its own. A block is read from the store once,
    /// when it first appears — reopening a running group, or a member joining mid-run — and after that only when a
    /// <see cref="RunLootCapturedEvent"/> names its run, so <see cref="_RefreshGroupTotalIsk"/> can sum the blocks on
    /// every clock tick without a single database read of its own (ET-211).
    /// </summary>
    private void _SyncLootOverview()
    {
        if (LootOverview is null)
            return;

        List<(Guid RunId, long CharacterId, string Name)> runs = Participants.Count > 0
            ? [.. Participants.Select(participant => (participant.RunId, (long)participant.CharacterId, participant.CharacterName))]
            : RunId is { } ownRunId && _runCharacterId is { } ownCharacterId
                ? [(ownRunId, ownCharacterId, _runCharacterName ?? $"Char {ownCharacterId}")]
                : [];
        foreach ((Guid runId, long characterId, string name) in runs)
        {
            bool isNew = LootOverview.Characters.All(block => block.RunId != runId);
            ActivityLootCharacterViewModel block = LootOverview.Show(runId, characterId, name);
            block.Loot.IsLocked = RunState is ActivityRunState.Saved;
            if (isNew)
                _ = block.Loot.RefreshAsync();
        }

        LootOverview.Keep([.. runs.Select(run => run.RunId)]);
    }

    /// <summary>
    /// A fleet member's sample, straight off the bus <c>FleetMetricPublisher</c> puts them on. Held per member so
    /// the envelope is re-taken over the whole fleet each time, not over the one sample that just arrived — which is
    /// also the only way the FLEET section can say anything at all: nothing else tells this window a fleet exists.
    /// </summary>
    private void _OnFleetMetric(FleetMetricEvent integrationEvent)
    {
        MetricSample sample = integrationEvent.Data;
        if (FleetId is { } fleetId && sample.FleetId != fleetId)
            return;

        if (sample.Kind is MetricKind.Loot or MetricKind.Bounty)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                (decimal? Loot, decimal? Bounty) held = _fleetIsk.GetValueOrDefault(sample.CharacterId);
                _fleetIsk[sample.CharacterId] = sample.Kind == MetricKind.Loot
                    ? held with { Loot = (decimal)sample.Value }
                    : held with { Bounty = (decimal)sample.Value };
                OnPropertyChanged(nameof(IsFleetShown));
                ApplyFleetEnvelope([.. _fleetLocations.Values], DateTime.UtcNow);
            });
            return;
        }

        if (sample.Kind != MetricKind.Location)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _fleetLocations[sample.CharacterId] = sample;
            OnPropertyChanged(nameof(IsFleetShown));
            ApplyFleetEnvelope([.. _fleetLocations.Values], DateTime.UtcNow);
        });
    }

    public void ApplyFleetEnvelope(IReadOnlyList<MetricSample> samples, DateTime receivedUtc)
    {
        List<MetricSample> members = samples
            .Where(sample => sample.Kind == MetricKind.Location)
            .GroupBy(sample => sample.CharacterId)
            .Select(group => group.OrderByDescending(sample => sample.UnixMs).First())
            .ToList();

        FleetMemberCount = members.Count;
        _SyncFleetMembers(members);

        if (!_IsInPocket)
        {
            AnchoredFleetMemberCount = 0;
            Refresh(receivedUtc);
            return;
        }

        List<DateTime> anchors = members
            .Select(sample => AbyssalSpace.AnchorFromWire(sample.AbyssalAnchorMs, sample.UnixMs, receivedUtc))
            .OfType<DateTime>()
            .ToList();

        AnchoredFleetMemberCount = anchors.Count;

        // Only this pilot's own anchor starts this window (ET-246). Each anchor is one member's own way in, and taking
        // the earliest of them started every window in the fleet the moment the first pilot jumped.
        if (RunState is ActivityRunState.NotStarted && _ActingCharacterId() is { } own
            && members.FirstOrDefault(sample => sample.CharacterId == own) is { } mine
            && AbyssalSpace.AnchorFromWire(mine.AbyssalAnchorMs, mine.UnixMs, receivedUtc) is { } ownAnchor)
        {
            AnchorUtc = ownAnchor;
            StoppedAtUtc = null;
            _startedOnEntry = true;
            RunState = ActivityRunState.Running;
            _OnRunWatched();
            // A run nobody pressed START for still needs its row, or the loot has nothing to attach to.
            if (RunId is null)
                _ = _BeginEstimatedRunAsync(ownAnchor);
        }

        Refresh(receivedUtc);
    }

    /// <summary>
    /// Bring the member rows in line with the samples that just arrived. Rows are kept and updated rather than
    /// rebuilt, so a name that public ESI has already resolved is not thrown away every second — and a member whose
    /// samples stop coming disappears, because this list is only ever a list of who is heard from.
    /// </summary>
    private void _SyncFleetMembers(IReadOnlyList<MetricSample> members)
    {
        foreach (int characterId in members.Select(sample => sample.CharacterId).Concat(_fleetIsk.Keys).Distinct())
            _RowFor(characterId);

        foreach (MetricSample sample in members)
            _RowFor(sample.CharacterId).LocationText = sample.AbyssalAnchorMs > 0
                ? "in abyssal space"
                : sample.Text ?? "not sharing a system";

        foreach (ActivityFleetMemberViewModel row in FleetMembers)
        {
            (decimal? Loot, decimal? Bounty) figures = _fleetIsk.GetValueOrDefault(row.CharacterId);
            row.LootIsk = figures.Loot;
            row.BountyIsk = figures.Bounty;
        }

        foreach (ActivityFleetMemberViewModel gone in FleetMembers
                     .Where(row => members.All(sample => sample.CharacterId != row.CharacterId)
                                   && !_fleetIsk.ContainsKey(row.CharacterId)).ToList())
            FleetMembers.Remove(gone);

        OnPropertyChanged(nameof(IsFleetShown));
        // The rows changed under the same collection; said as a change of it, for the sections that read it.
        OnPropertyChanged(nameof(FleetMembers));
    }

    /// <summary>The row for a member, made on first sight. A name public ESI has already resolved is not thrown away
    /// and asked for again every second, which is why rows are kept and updated rather than rebuilt.</summary>
    private ActivityFleetMemberViewModel _RowFor(int characterId)
    {
        if (FleetMembers.FirstOrDefault(row => row.CharacterId == characterId) is { } existing)
            return existing;

        ActivityFleetMemberViewModel row = new(characterId);
        FleetMembers.Add(row);
        _ = _ResolveFleetMemberNameAsync(row);
        return row;
    }

    /// <summary>Best-effort: an unresolved id keeps its "Char 90000001" label, which is still a member you can
    /// count.</summary>
    private async Task _ResolveFleetMemberNameAsync(ActivityFleetMemberViewModel member)
    {
        if (await _NameOfAsync(member.CharacterId) is { } name)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => member.Name = name);
    }

    /// <summary>The registry first — a local character is known without asking anyone — then public ESI, the same
    /// route the fleet overlay resolves its rows by. Null is "could not be named", never a placeholder: what to
    /// show instead is the caller's decision, and the two callers here answer it differently.</summary>
    private async Task<string?> _NameOfAsync(int characterId)
    {
        if (_services.GetService<ICharacterRegistry>() is { } registry
            && (await registry.GetAllAsync()).FirstOrDefault(c => c.EsiCharacterId == characterId) is { } local)
            return local.Name;

        if (_services.GetService<IExternalCharacterLookup>() is not { } lookup)
            return null;

        ExternalCharacterInfo info = await lookup.LookupAsync(characterId);
        return info.Exists ? info.Name : null;
    }

    /// <param name="siteName">What the fleet says is being flown, for a window that has nothing of its own. Taken
    /// only after <see cref="_AdoptRunningRunAsync"/> has had its say: a name from elsewhere is not a signature this
    /// pilot copied, and setting it first made adopt read the member's own run as a different site and park it.</param>
    private async Task _BeginEstimatedRunAsync(DateTime anchorUtc, string? siteName = null)
    {
        if (await _AdoptRunningRunAsync())
            return;

        // Same two-step as StartRunAsync: a pilot resolved just now by the line above still deserves the adopt this
        // call opened with, or a fleet anchor would start a second row under a character who already has one open.
        if (!await _ResolveCharacterAsync(mayAsk: false) || await _AdoptRunningRunAsync())
            return;

        SignatureName ??= siteName;
        await _StoreRunAsync(anchorUtc);
    }

    /// <summary>
    /// Drives the "own character" bounty figure this window shows for whichever character the column is on right
    /// now. The row itself is already written by <see cref="GamelogClientService.AddBountyAsync"/> straight onto
    /// its own run's <c>RunBountyEntry</c> (ET-219) — this handler is display-only, filtered by character because
    /// the section says "own character" and this client watches every pilot's log at once.
    /// </summary>
    private void _OnBountyObserved(string characterName, BountyEvent bounty)
    {
        if (RunState != ActivityRunState.Running || IsInsideAbyssal
            || !string.Equals(characterName, _runCharacterName, StringComparison.OrdinalIgnoreCase))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BountyIsk += bounty.Isk;
            Refresh(DateTime.UtcNow);
        });
    }

    /// <summary>
    /// Where the pilot is, from the one place the app already knows it: the gamelog service's own snapshot, which
    /// carries the system its jump/undock lines wrote and the abyssal anchor ESI observed. Read on the tick rather
    /// than pushed, so the abyssal countdown moves with the clock like every other readout of it.
    /// </summary>
    private void _RefreshLocation(DateTime nowUtc)
    {
        _canSeeCrossing = false;
        if (_gamelog is null || _runCharacterName is null)
            return;

        CharacterMetricsSnapshot snapshot = _gamelog.Snapshot(_runCharacterName);
        _canSeeCrossing = snapshot.LocationUnavailableReason is null;
        bool? wasInside = InsideAbyssal;
        if (snapshot.AbyssalAnchor is not null)
            InsideAbyssal = true;
        else if (snapshot.Location is not null && snapshot.LocationUnavailableReason is null)
            InsideAbyssal = false;

        _StartOrStopOnAbyssalCrossing(wasInside, snapshot.AbyssalAnchor, nowUtc);

        // Same rule as DpsViewModel.LocationDisplay, and for the same reason (ET-71): a pilot known to be out of the
        // game reads as that, never as the system they undocked in hours ago.
        bool offline = _runCharacterId is { } characterId
            && _services.GetService<ILocalCharacterPresence>()?.IsInGame(characterId, _runCharacterName) is false;

        SolarSystem = offline ? null : snapshot.Location;
        LocationDisplay = offline
            ? "offline"
            : AbyssalSpace.Describe(snapshot.Location, snapshot.AbyssalAnchor, nowUtc)
              ?? EsiLocationReasonText.Describe(snapshot.LocationUnavailableReason);
    }

    /// <summary>
    /// Let the location watch press this window's own START and STOP. STOP stays the pause it is everywhere else:
    /// the row is left open, and SAVE and DISCARD stay the pilot's.
    /// </summary>
    private void _StartOrStopOnAbyssalCrossing(bool? wasInside, DateTime? anchorUtc, DateTime nowUtc)
    {
        if (!_IsInPocket)
            return;

        // The anchor leads, because it is the only thing that rules out a cold start inside: this branch is entered
        // on wasInside null too, so the null check is what holds it. CharacterMetrics.SeenInside anchors ONLY when
        // a poll has already placed this pilot outside, so a client that came up with them already in a pocket has
        // no anchor and starts nothing — a start invented on a twenty-minute limit is worse than none.
        if (anchorUtc is { } lastSeenOutsideUtc && wasInside is not true && InsideAbyssal is true
            && RunState is ActivityRunState.NotStarted)
            LastAbyssalEntry = _StartOnAbyssalEntryAsync(lastSeenOutsideUtc);
        // On the crossing ESI observed, not on the state it reports: keyed on the state, the first outside reading
        // would stop a run that was started by hand and never taken in. Coming out stops it whoever started it.
        else if (wasInside is true && InsideAbyssal is false && RunState is ActivityRunState.Running)
            StopRun(nowUtc);
    }

    /// <summary>The pending automatic start, so a test can await what a location reading set going.</summary>
    internal Task LastAbyssalEntry { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The run's <c>StartedAtUtc</c> becomes <paramref name="lastSeenOutsideUtc"/>: <c>AbyssalAnchor</c>, which
    /// <c>CharacterMetrics.SeenInside</c> set to the timestamp of the last poll that placed this pilot outside —
    /// never the moment the crossing was noticed. Entry is written nowhere and the watch only looks every
    /// <c>EsiLocationMonitor.PollInterval</c>, so this stays the floor <see cref="ClockHint"/> says it is.
    /// </summary>
    private async Task _StartOnAbyssalEntryAsync(DateTime lastSeenOutsideUtc)
    {
        try
        {
            _startedOnEntry = true;
            StartManualRun(lastSeenOutsideUtc);
            await _BeginEstimatedRunAsync(lastSeenOutsideUtc);
        }
        catch (Exception ex)
        {
            // Put back to standing by rather than left on a clock with no row behind it: nobody pressed this
            // button, so a pilot who is already in a pocket would have no reason to doubt it was being recorded.
            // Same treatment _StartOnArrivalAsync gives the other start nobody pressed.
            RunState = ActivityRunState.NotStarted;
            AnchorUtc = null;
            _startedOnEntry = false;
            OnPropertyChanged(nameof(IsStartButtonVisible));
            _services.GetService<IToastService>()?.Show("Run not started",
                $"Going into the abyss did not start this run: {ex.Message}. Press START to record it.",
                ToastKind.Error);
        }
    }

    /// <summary>
    /// Every other own toon riding along in a run whose clock is per pilot (ET-210, ET-243) on its own way in and
    /// out, exactly as <see cref="_StartOrStopOnAbyssalCrossing"/> already does for the acting character — a pocket
    /// only ever shows a pilot their own crossing, so a toon picked alongside this window's own character has to be
    /// read from its own gamelog, never assumed from this one's (ET-250).
    ///
    /// A toon still in <see cref="_ownLegsPending"/> has no row yet: it gets one, at its own anchor, the moment its
    /// own snapshot shows it inside. One already running (a row in <see cref="Participants"/>) is watched for its own
    /// way out, and for a way back in if it had already left — the same pause-and-resume STOP already is for the
    /// acting character (Raymond, 2026-09-02), just on this toon's own account instead of a button press.
    /// </summary>
    private void _RefreshOwnPilotLegs(DateTime nowUtc)
    {
        if (!RunType.ClockPerPilot || _gamelog is null)
            return;

        foreach ((int Id, string Name) pending in _ownLegsPending.ToList())
        {
            CharacterMetricsSnapshot snapshot = _gamelog.Snapshot(pending.Name);
            if (snapshot.AbyssalAnchor is not { } enteredAtUtc)
                continue;

            _ownLegsPending.Remove(pending);
            _ownLegWasInside[pending.Id] = true;
            _ = _StartOwnPilotLegAsync(pending.Id, pending.Name, enteredAtUtc);
        }

        foreach (RunParticipantViewModel sibling in Participants.Where(participant => participant.RunId != RunId))
        {
            CharacterMetricsSnapshot snapshot = _gamelog.Snapshot(sibling.CharacterName);
            bool wasInside = _ownLegWasInside.GetValueOrDefault(sibling.CharacterId, true);
            bool isInside = snapshot.AbyssalAnchor is not null;
            if (wasInside && !isInside && snapshot.Location is not null && snapshot.LocationUnavailableReason is null)
            {
                _ownLegWasInside[sibling.CharacterId] = false;
                _ = _StopOwnPilotLegAsync(sibling.RunId, sibling.CharacterId, nowUtc);
            }
            else if (!wasInside && isInside)
            {
                _ownLegWasInside[sibling.CharacterId] = true;
                _ = _ResumeOwnPilotLegAsync(sibling.RunId, sibling.CharacterId, nowUtc);
            }
        }
    }

    /// <summary>One own toon's own row, made at its own anchor rather than this window's — <see cref="_ResolveFitAsync"/>
    /// and the rest of <see cref="_SendAdditionalStartRunCommandAsync"/> already read per character.</summary>
    private async Task _StartOwnPilotLegAsync(int characterId, string characterName, DateTime enteredAtUtc)
    {
        if (_services.GetService<CqrsDispatcher>() is null)
            return;

        using var scope = _services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        await _SendAdditionalStartRunCommandAsync(
            dispatcher, characterId, characterName, enteredAtUtc, _ResolveSolarSystemId());
        await _RefreshParticipantsAsync();
    }

    /// <summary>One own toon's own way out — its row alone, never this window's or another sibling's
    /// (<see cref="_SetStoredRunStoppedAsync"/> already leaves every other own toon's row untouched for this same
    /// reason). Announced under the pilot's own id, same as this window's own STOP (ET-250).</summary>
    private async Task _StopOwnPilotLegAsync(Guid runId, int characterId, DateTime stoppedAtUtc)
    {
        if (_services.GetService<CqrsDispatcher>() is not null)
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new SetRunStoppedCommand(runId, stoppedAtUtc));
        }

        _AnnouncePilotStopToFleet(characterId, stoppedAtUtc);
    }

    /// <summary>One own toon's own way back in after its own way out, before this pilot's group has saved or
    /// discarded it — the pause SAVE and DISCARD still answer for the whole group (ET-210), only STOP is per toon.</summary>
    private async Task _ResumeOwnPilotLegAsync(Guid runId, int characterId, DateTime resumedAtUtc)
    {
        if (_services.GetService<CqrsDispatcher>() is not null)
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new SetRunStoppedCommand(runId, null));
        }

        _AnnouncePilotResumeToFleet(characterId, resumedAtUtc);
    }

    public void Dispose()
    {
        if (_gamelog is not null)
            _gamelog.BountyObserved -= _OnBountyObserved;

        foreach (RunWindowSection section in _AllSections())
            section.Dispose();

        _metricSubscription?.Dispose();
        _lootSubscription?.Dispose();
        _fleetRunStartedSubscription?.Dispose();
        _fleetRunStoppedSubscription?.Dispose();
        _fleetRunDiscardedSubscription?.Dispose();
        _fleetRunAbyssalUpdatedSubscription?.Dispose();
        _fleetRunPreparedSubscription?.Dispose();
        _fleetPilotStoppedSubscription?.Dispose();
        _fleetPilotResumedSubscription?.Dispose();
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>The run on screen is on the clock — started, resumed, joined, adopted or anchored by the fleet — and
    /// every section hears it.</summary>
    private void _OnRunWatched()
    {
        foreach (RunWindowSection section in _AllSections())
            section.OnRunStarted();
        _RefreshSummaries();
    }

    /// <summary>The run is committed or thrown away, and every section lets go of what it was collecting — the whole
    /// group's, since STOP, SAVE and DISCARD act on the whole group (ET-210).</summary>
    private void _OnRunClosed()
    {
        foreach (RunWindowSection section in _AllSections())
            section.OnRunClosed();
        _ownLegsPending.Clear();
        _ownLegWasInside.Clear();
        _RefreshSummaries();
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────────────

    private void _RefreshClock(DateTime nowUtc)
    {
        DateTime effectiveNow = EffectiveStopUtc ?? nowUtc;
        ClockLabel = _IsInPocket
            ? RunState == ActivityRunState.Stopped ? "TIME LEFT AT STOP" : "TIME LEFT"
            : "ELAPSED";

        if (EffectiveStartUtc is not { } start)
        {
            ClockText = NoClock;
            IsClockWarning = false;
            IsClockCritical = false;
            StartText = "not started";
            EndText = "not started";
            return;
        }

        StartText = _LocalTime(start);

        if (!_IsInPocket)
        {
            ClockText = _Elapsed(effectiveNow - start);
            IsClockWarning = false;
            IsClockCritical = false;
            EndText = EffectiveStopUtc is { } stopped ? _LocalTime(stopped) : "still running";
            return;
        }

        // END is the deadline, not the moment the last pilot got out: at RunLimit the ship and the pod are gone,
        // and that is the only end time worth putting on screen while the run is still going.
        EndText = EffectiveStopUtc is { } stoppedAt ? _LocalTime(stoppedAt) : _LocalTime(start + AbyssalSpace.RunLimit);

        // No remaining time is the loudest state there is, not the absence of one: past the deadline we are already
        // wrong about something, and a lifted `null <= CriticalAt` would quietly have shown that in the resting
        // colour.
        var remaining = AbyssalSpace.Remaining(start, effectiveNow);
        ClockText = remaining is { } left ? _Elapsed(left) : NoClock;
        IsClockCritical = remaining is null || remaining <= CriticalAt;
        IsClockWarning = remaining > CriticalAt && remaining <= WarningAt;
    }

    /// <summary>
    /// Whether the window is waiting for this pilot's way in, and whether that way in will be seen at all: the location
    /// watch can only start a run for a pilot it knows and can locate, and an armed banner over a crossing nobody will
    /// report would be the silent failure this line exists to replace.
    /// </summary>
    private void _RefreshArmed()
    {
        IsArmedShown = RunType.ClockPerPilot && RunState is ActivityRunState.NotStarted && _pendingCopy is null;
        IsArmed = IsArmedShown && _canSeeCrossing;
        string filament = HasWeatherAndTier ? $" — {AbyssalFilamentName.From(TierIndex, Weather?.Name)}" : string.Empty;
        ArmedText = !IsArmedShown
            ? string.Empty
            : !_canSeeCrossing
                ? _runCharacterName is { } pilot
                    ? $"This client cannot see where {pilot} is, so going in will not start the run. "
                      + "Press START when you jump in."
                    : "Nobody is picked for this run yet, so going in will not start it. Pick the pilot above, "
                      + "or press START when you jump in."
                : GroupCode is null
                    ? "Starts by itself when you jump into the abyss. START starts it now."
                    : Authority.IsFleetCommander
                        ? $"Offered to the fleet{filament}. Your run starts by itself when you jump into the abyss, "
                          + "and each pilot's starts when they do. START starts yours now."
                        : $"Fleet run{filament}. Starts by itself when you jump into the abyss — not when the "
                          + "commander does. START starts it now.";
    }

    /// <summary>
    /// The fleet's own clock (ET-243): from the first pilot in to the last one out, over every leg announced under this
    /// run's code, with this pilot's own taken from the window rather than from the echo of its own announcement.
    ///
    /// A leg that never reported its way out counts as out once the pocket is gone: nobody is inside longer than
    /// <see cref="AbyssalSpace.RunLimit"/>, and a lost announcement — a server that does not relay it yet, a client
    /// that went down inside — would otherwise read "still in" for the rest of the evening.
    /// </summary>
    private void _RefreshFleetClock(DateTime nowUtc)
    {
        Dictionary<int, (DateTime In, DateTime? Out)> legs = [];
        if (RunType.ClockPerPilot && GroupCode is { } groupCode && FleetId is not null)
        {
            foreach (FleetRunLeg leg in _fleetLegs?.Of(groupCode) ?? [])
            {
                DateTime collapsedAtUtc = leg.StartedAtUtc + AbyssalSpace.RunLimit;
                legs[leg.CharacterId] = (leg.StartedAtUtc,
                    leg.StoppedAtUtc ?? (nowUtc >= collapsedAtUtc ? collapsedAtUtc : (DateTime?)null));
            }

            if (_runCharacterId is { } own && EffectiveStartUtc is { } ownStart
                && RunState is ActivityRunState.Running or ActivityRunState.Stopped)
                legs[own] = (ownStart, RunState is ActivityRunState.Running ? null : EffectiveStopUtc);
        }

        HasFleetClock = legs.Count > 0;
        if (!HasFleetClock)
        {
            FleetClockText = string.Empty;
            IsWaitingForFleet = false;
            return;
        }

        DateTime firstIn = legs.Values.Min(leg => leg.In);
        int inside = legs.Values.Count(leg => leg.Out is null);
        if (inside == 0)
        {
            DateTime lastOut = legs.Values.Max(leg => leg.Out ?? leg.In);
            FleetClockText =
                $"first in {_LocalTime(firstIn)} · last out {_LocalTime(lastOut)} · {_Elapsed(lastOut - firstIn)}";
            IsWaitingForFleet = false;
            if (RunState is ActivityRunState.NotStarted)
                _EndedWithoutThisPilot();
            return;
        }

        IsWaitingForFleet = RunState is ActivityRunState.Stopped;
        string stillIn = inside == 1 ? "1 pilot" : $"{inside} pilots";
        FleetClockText = IsWaitingForFleet
            ? $"first in {_LocalTime(firstIn)} · {_Elapsed(nowUtc - firstIn)} so far · you are out, waiting for {stillIn}"
            : $"first in {_LocalTime(firstIn)} · {_Elapsed(nowUtc - firstIn)} so far · {stillIn} in";
    }

    /// <summary>
    /// Everyone who went in is out and this pilot never went in — the one who stayed behind, or the hauler outside. The
    /// run is over for the fleet, so this window lets go of it: going in now would be a pocket of its own, not a late
    /// leg of one that has already ended, and a window left armed on it would say otherwise.
    /// </summary>
    private void _EndedWithoutThisPilot()
    {
        GroupCode = null;
        RunNoticeText = "Everyone who went in on this fleet run is out, so it ended without you. Nothing of yours was "
                        + "recorded. Close this window, or fly the next pocket on your own.";
        _RefreshArmed();
    }

    /// <summary>
    /// The group's running total, clock-driven exactly like <see cref="_RefreshClock"/> — nothing here does a
    /// database or network round trip, so ticking every second costs no more than formatting a string does.
    ///
    /// The window only gathers what the run is made of so far; <see cref="IskContributors"/> adds it up, the same
    /// registry the saved detail screen, the runs overview and UNFINISHED read (ET-256), so what each source counts —
    /// a mission's bonus judged at STOP, never its stated Bounty line — is decided there and nowhere here.
    ///
    /// Bounty is summed per participant through <see cref="GamelogClientService.GetFleetRunBounty"/> when a real
    /// fleet is involved — the same switch-independent, synchronous source SAVE already uses for a group — rather
    /// than the acting character's own <see cref="BountyIsk"/>, which only ever holds whichever character the
    /// column was showing while it came in. An own-toon group with no fleet at all (ET-257) has no fleet tally to
    /// read, so it falls back to each participant's own <see cref="RunParticipantViewModel.BountyIsk"/> — this run's
    /// own <c>RunBountyEntry</c> total (ET-219), refreshed alongside the rest of <see cref="Participants"/>. A solo
    /// run (nothing to switch away from) keeps using <see cref="BountyIsk"/>, since there is no group to sum.
    ///
    /// Loot is summed the same way, per participant, through <see cref="LootOverview"/>'s blocks (ET-211, ET-215) —
    /// now that a capture is attributed to the character whose client actually copied it, a group's total is the sum
    /// of every participant's own run, not whichever one <see cref="RunLoot"/> happens to be showing. A solo run has
    /// nothing to sum but its own <see cref="RunLoot"/>.
    ///
    /// One <see cref="RunIskFacts"/> per participant, not one pre-summed record for the group: the same shape
    /// <see cref="RunIskFactsReader"/> hands the registry for a saved activity, so the mission-reward line every own
    /// toon's run carries a copy of is deduplicated by <see cref="RewardIskContributor"/> exactly as it already is
    /// there, instead of a second, window-only summing rule.
    /// </summary>
    private void _RefreshGroupTotalIsk(DateTime nowUtc)
    {
        bool isGroup = Participants.Count > 1;
        List<RunIskParameter> parameters = [.. PendingParameters.Select(parameter => new RunIskParameter(
            parameter.ParameterKey, parameter.Amount, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))];
        var consumables = _sections.GetValueOrDefault(RunSectionId.Consumables) as ConsumablesWindowSectionViewModel;

        List<RunIskFacts> runs = isGroup
            ? [.. Participants.Select(participant =>
                {
                    // A real fleet's own bounty comes from GamelogClientService's per-fleet tally (synchronous,
                    // and the only place a fleet mate's own bounty ever lands, since their gamelog is never this
                    // machine's own to watch). An own-toon group with no fleet at all (ET-257) has no such tally —
                    // every toon's own bounty already lives in its own run's RunBountyEntry rows (ET-219), read back
                    // through Participants' own cache instead.
                    decimal bountyIsk = FleetId is { } fleetId && _gamelog is not null
                        ? _gamelog.GetFleetRunBounty(fleetId, participant.CharacterId)
                        : participant.BountyIsk;
                    RunLootViewModel? loot = LootOverview?.Characters
                        .FirstOrDefault(character => character.RunId == participant.RunId)?.Loot;
                    (decimal? cost, bool has) = _ConsumableFacts(consumables, participant.RunId);
                    return new RunIskFacts
                    {
                        BountyIsk = bountyIsk,
                        LootIskNet = loot?.NetIsk,
                        HasLoot = loot?.HasCaptures ?? false,
                        ConsumableIskCost = cost,
                        HasConsumables = has,
                        Parameters = parameters,
                        StoppedAtUtc = EffectiveStopUtc
                    };
                })]
            : [_SoloRunIskFacts(consumables, parameters)];

        IskBreakdown isk = IskContributors.Breakdown(runs, nowUtc);
        HasGroupTotalIsk = isk.HasFigure;
        GroupTotalIskText = IskFormat.Whole(isk.Total) + IskFormat.ExpectedPart(isk);
    }

    private RunIskFacts _SoloRunIskFacts(ConsumablesWindowSectionViewModel? consumables, IReadOnlyList<RunIskParameter> parameters)
    {
        (decimal? cost, bool has) = RunId is { } runId ? _ConsumableFacts(consumables, runId) : (null, false);
        return new RunIskFacts
        {
            BountyIsk = BountyIsk,
            LootIskNet = RunLoot?.NetIsk,
            HasLoot = RunLoot?.HasCaptures ?? false,
            ConsumableIskCost = cost,
            HasConsumables = has,
            Parameters = parameters,
            StoppedAtUtc = EffectiveStopUtc
        };
    }

    /// <summary>What CONSUMABLES has for one run — its own confirmed count, priced against the section's shared
    /// filament unit price (ET-249). No section built yet (a non-abyssal run) reads the same as no count confirmed.</summary>
    private static (decimal? Cost, bool Has) _ConsumableFacts(ConsumablesWindowSectionViewModel? consumables, Guid runId)
    {
        ConsumableRowViewModel? row = consumables?.Rows.FirstOrDefault(r => r.RunId == runId);
        if (row?.Count is not { } count)
            return (null, false);

        return (consumables!.UnitPrice is { } price ? count * price : null, true);
    }

    // The signature arrives after construction, from the object initialiser the toast opens the window with — so the
    // shut ACTIVITY header has to be worked out again then, not only on the next clock tick.
    partial void OnSignatureNameChanged(string? value) => _RefreshSummaries();

    partial void OnMatchedSitesChanged(IReadOnlyList<SdeSite> value) => _RefreshSummaries();

    private void _RefreshSummaries()
    {
        foreach (RunWindowSection section in _AllSections())
            section.RefreshSummary();
    }

    /// <summary>
    /// A signature copied while this window is up. With no run going it simply becomes the window's site. With a
    /// run going on a DIFFERENT site it stops the clock and waits: that run is not this one, and ending it is SAVE
    /// or DISCARD — the player's call, never the window's. The copied site is held and applied the moment they do.
    /// </summary>
    /// <summary>
    /// The clipboard hands this over from a void call, so the work is tracked rather than dropped: a dispatcher
    /// that throws — a locked database is the one that happens — becomes a toast and a log line instead of an
    /// unobserved task, the same treatment <c>ClipboardLootCapture.StoreAndOfferAsync</c> gives its own write.
    ///
    /// Nothing races on the caller's side: <c>DialogService</c> only reaches here when a window is already up, and
    /// every branch after it either returns or activates that same window. It never builds a second one.
    /// </summary>
    public void ApplySignature(string? id, string? group, string name, IReadOnlyList<SdeSite> sites) =>
        LastSignature = _ApplySignatureSafelyAsync(id, group, name, sites);

    /// <summary>The pending hand-over, so a test can await what a void call started.</summary>
    internal Task LastSignature { get; private set; } = Task.CompletedTask;

    private async Task _ApplySignatureSafelyAsync(string? id, string? group, string name, IReadOnlyList<SdeSite> sites)
    {
        try
        {
            await ApplySignatureAsync(id, group, name, sites);
        }
        catch (Exception ex)
        {
            _services.GetService<IToastService>()?.Show("Site not switched",
                $"Could not close the open run to make room for {name}: {ex.Message}", ToastKind.Error);
            _SignatureDecision($"failed: {ex.Message}", name);
            return; // a switch that failed leaves a window nobody should start a run on
        }

        await _StartOnArrivalAsync();
    }

    /// <summary>
    /// Same rule as the adopt-on-open path, and deliberately the same method rather than a second copy of it: the
    /// window being open or closed decided which of the two ran, and fixing only one of them is what kept this bug
    /// alive through four attempts.
    /// </summary>
    public async Task ApplySignatureAsync(string? id, string? group, string name, IReadOnlyList<SdeSite> sites)
    {
        if (RunState is ActivityRunState.NotStarted || _IsSameRun(SignatureId, SignatureName, id, name))
        {
            _pendingCopy = null;
            _SetSignature(id, group, name, sites);
            _SignatureDecision("no run of another site was open", name);
            Refresh(DateTime.UtcNow);
            return;
        }

        // A run of another site was going, and it is the pilot's to end — never this window's. It used to be
        // discarded here on the spot whenever it was solo, which threw away what he was flying without asking
        // (Raymond, 2026-09-04). Now every run takes the one route a group run always took: the clock stops, the
        // copy waits, and SAVE, DISCARD or KEEP answers it. GroupCode no longer decides anything here, so this route
        // and _AdoptRunningRunAsync's own close-out no longer read the same — that one is about a run left in the
        // store rather than one being flown, and it keeps its discard until a report says otherwise. A copied signature
        // is a site, whatever this window is showing, so that is the kind the waiting window opens as.
        _pendingCopy = new PendingCopy(ActivityKind.Site, name, StartsOnArrival, id, group, sites, null, null, null, []);
        StopRun(DateTime.UtcNow);
        _SignatureDecision($"the open {SignatureName} run is not this one, so this waits", name);
        Refresh(DateTime.UtcNow);
    }

    private void _SetSignature(string? id, string? group, string name, IReadOnlyList<SdeSite> sites)
    {
        SignatureId = id;
        SignatureGroup = group;
        SignatureName = name;
        MatchedSites = sites;
    }

    /// <summary>
    /// Whether a copied signature is the run already on this window rather than a new one. EVE gives every scan its
    /// own id, and that is the only thing that tells "the site I am already in" from "another Sansha Refuge" —
    /// comparing site names made every repeat of the same site look like the run in progress, which is what kept
    /// handing Raymond a ticking clock when he scanned the next one (2026-09-02). The site name only stands in
    /// where an id is missing on either side, which is a run started before this carried one.
    /// </summary>
    private static bool _IsSameRun(string? storedId, string? storedSite, string? copiedId, string? copiedSite) =>
        storedId is { Length: > 0 } stored && copiedId is { Length: > 0 } copied
            ? string.Equals(stored, copied, StringComparison.OrdinalIgnoreCase)
            : string.Equals(storedSite, copiedSite, StringComparison.Ordinal);

    /// <summary>The mission half of <see cref="ApplySignature"/> — same rule, same reason (ET-158, applied to
    /// missions in ET-172 sub 4), and since Raymond's 2026-09-04 report the same waiting path too: a mission copied
    /// over a run in progress asks instead of overwriting it, which is the real case ET-176 was waiting for.</summary>
    public void ApplyMission(int? agentId, int? missionLevel, int? solarSystemId, string? agentName,
        IReadOnlyList<RunParameterInput> parameters) =>
        LastMission = _ApplyMissionSafelyAsync(agentId, missionLevel, solarSystemId, agentName, parameters);

    /// <summary>The pending hand-over, so a test can await what a void call started.</summary>
    internal Task LastMission { get; private set; } = Task.CompletedTask;

    private async Task _ApplyMissionSafelyAsync(int? agentId, int? missionLevel, int? solarSystemId, string? agentName,
        IReadOnlyList<RunParameterInput> parameters)
    {
        // A run that is not this one was going: the same wait a copied signature gets, for the same reason. Guarded
        // on a named agent because the waiting copy is what a new window is later built from, and a window has to
        // open on something the pilot recognises rather than on a stand-in this method made up. A copied mission opens
        // a mission window, whatever this one is showing.
        if (agentName is { Length: > 0 } waiting && RunState is not ActivityRunState.NotStarted
            && !_IsSameRun(SignatureId, SignatureName, null, waiting))
        {
            _pendingCopy = new PendingCopy(ActivityKind.Mission, waiting, StartsOnArrival, null, null, [], agentId,
                missionLevel, solarSystemId, parameters);
            StopRun(DateTime.UtcNow);
            _SignatureDecision($"the open {SignatureName} run is not this one, so this waits", waiting);
            Refresh(DateTime.UtcNow);
            return;
        }

        MissionAgentId = agentId;
        MissionLevel = missionLevel;
        MissionSolarSystemId = solarSystemId;
        // A mission has no site name of its own (ET-172 sub 4) — the agent's name is the one thing this window can
        // show for it, carried on the same field a site's name travels on rather than a new one just for this.
        SignatureName = agentName;
        PendingParameters = parameters;
        await _StartOnArrivalAsync();
    }

    /// <summary>
    /// What this window did with a copied signature, and why — the line that says which of the two routes ran,
    /// after this bug survived four attempts because that was invisible.
    ///
    /// ponytail: temporary instrument, kept on the Information diagnostic channel (ET-139) so it still reaches
    /// app-errors.jsonl without claiming an ordinary copy is an error. Take it out once Raymond confirms the
    /// site switch behaves — not before.
    /// </summary>
    private void _SignatureDecision(string what, string name) =>
        _services.GetService<ILoggerFactory>()?.CreateLogger<ActivityWindowViewModel>().LogDiagnostic(
            "Copied signature {Signature}: {What} (run {RunId}, state {State}, group {Group}, fleet {Fleet}).",
            name, what, RunId, RunState, GroupCode, FleetId);

    /// <summary>SAVE is the lock, and so is ET-179 finishing a run left standing: a committed run's loot carries no
    /// controls at all, which is the whole difference between an editable section and a fixed one.</summary>
    partial void OnRunStateChanged(ActivityRunState value)
    {
        if (RunLoot is not null)
            RunLoot.IsLocked = value is ActivityRunState.Saved;
        foreach (ActivityLootCharacterViewModel block in LootOverview?.Characters ?? [])
            block.Loot.IsLocked = value is ActivityRunState.Saved;
    }

    private static string _LocalTime(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>mm:ss, counting past the hour rather than wrapping — a site run is not bounded by anything.</summary>
    private static string _Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes:00}:{span.Seconds:00}");
    }
}
