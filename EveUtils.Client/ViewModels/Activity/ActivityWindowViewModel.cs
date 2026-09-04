using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Control;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The activity window (ET-98): a run you are still flying, rather than a form you fill in once it is over. That is
/// the whole difference from every other tracker, and it is why the clock is the largest thing on it.
///
/// START creates the stored <c>Run</c> and the window follows that row from there — the clock, the loot, the enemies
/// and the bounties all hang off one id, so "a run is running" means the same thing here as it does in the database.
/// STOP only stops the clock: the row stays open until SAVE or DISCARD, so loot copied after the last rat still
/// lands on the run it came from.
/// </summary>
public sealed partial class ActivityWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Where the manual weather and tier are remembered. Under <c>ui.</c> with the other shell prefs, and
    /// remembered at all because you fly the same tier several runs in a row — which is what turns two clicks a run
    /// into two clicks an evening.</summary>
    public const string WeatherSettingKey = "ui.activity.weather";

    public const string TierSettingKey = "ui.activity.tier";

    /// <summary>Remembered for the same reason as the tier: you loot the same way several runs in a row.</summary>
    public const string LootStrategySettingKey = "ui.activity.lootstrategy";

    /// <summary>Once a second. The readout is a clock, and a clock cannot be read faster than it ticks.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>The seven abyssal tiers, index = the T-number the filament is sold under.</summary>
    public static IReadOnlyList<string> Tiers { get; } =
        ["Tranquil", "Calm", "Agitated", "Fierce", "Raging", "Chaotic", "Cataclysmic"];

    /// <summary>How much of the pocket you opened. Kinds loot in different vocabularies, so each gets its own list
    /// rather than one that half fits each — and a kind with nothing sensible to offer gets none.</summary>
    public static IReadOnlyList<string> AbyssalLootStrategies { get; } =
        ["bioadaptive only", "bioadaptive + triglavian", "all cans"];

    public static IReadOnlyList<string> SiteLootStrategies { get; } = ["blitzed", "cleared", "full clear"];

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

    // The bounty lines seen while this run was running, with their own times — what SAVE writes as the run's
    // RunBountyEntry rows, and what the section adds up meanwhile.
    private readonly List<RunBountyEntryInput> _bounties = [];

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
    private (string? Id, string? Group, string Name, IReadOnlyList<SdeSite> Sites)? _pendingSignature;
    private string? _runCharacterName;
    private int? _commanderNameId;
    private string? _commanderName;
    private ShipFitDetectionReading? _fitReading;
    private RunEnemyObservationCollector? _enemyObservations;

    public ActivityWindowViewModel(ActivityKind kind, IServiceProvider services)
    {
        Kind = kind;
        _services = services;
        _gamelog = services.GetService<GamelogClientService>();
        if (_gamelog is not null)
        {
            _gamelog.CombatObserved += _OnCombatObserved;
            _gamelog.BountyObserved += _OnBountyObserved;
        }

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
        RunLoot = services.GetService<CqrsDispatcher>() is { } dispatcher
            ? new RunLootViewModel(dispatcher, services.GetService<IAppraisalProvider>())
            : null;
        if (RunLoot is not null)
            RunLoot.PropertyChanged += (_, _) => _RefreshSummaries();

        WeatherChoices = AbyssalWeather.All
            .Select((weather, index) => new ActivityChoice
            {
                Index = index,
                Label = weather.Name,
                Tooltip = $"{weather.EnvironmentName} — {weather.Bonus}, penalty on {weather.PenaltyTarget}"
            })
            .ToList();

        TierChoices = Tiers
            .Select((tier, index) => new ActivityChoice { Index = index, Label = tier })
            .ToList();

        LootStrategyChoices = LootStrategies
            .Select((strategy, index) => new ActivityChoice { Index = index, Label = strategy })
            .ToList();

        Sections = [Activity, Enemies, Fit, Fleet, Bounty, Loot];
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Which kind of run this window is showing — the same value the store files it under, given to the
    /// window rather than worked out here (ET-174 AC-3). Fixed for the life of the window: a run does not turn into
    /// another kind halfway through.</summary>
    public ActivityKind Kind { get; }

    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations => _enemyObservations?.Observations ?? [];

    public RunLootViewModel? RunLoot { get; }

    /// <summary>Only for what is genuinely the abyss's own: the 20-minute deadline, the tier and weather, the
    /// per-member anchors. Never for "and everything else is a site" — that is what this window used to do.</summary>
    public bool IsAbyssal => Kind == ActivityKind.Abyssal;

    // ── The run ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The envelope START — the earliest moment anyone in the run could be proved to be in it: the stored
    /// run's own <c>StartedAtUtc</c> when a pilot pressed START, and the <c>Min()</c> over the fleet's re-based
    /// anchors when the fleet's samples put it earlier.</summary>
    [ObservableProperty] private DateTime? _anchorUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    [NotifyPropertyChangedFor(nameof(IsStartButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsStopButtonVisible))]
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    [NotifyPropertyChangedFor(nameof(IsLocationShown))]
    private string? _solarSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    [NotifyPropertyChangedFor(nameof(IsLocationShown))]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    [NotifyPropertyChangedFor(nameof(IsInsideAbyssal))]
    private bool? _insideAbyssal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    [NotifyPropertyChangedFor(nameof(IsLocationShown))]
    private string? _locationDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    private long _bountyIsk;

    /// <summary>What was looted and what was left. Without it "19 minutes" says nothing — which is why it is set
    /// here rather than only displayed.</summary>
    [ObservableProperty] private string? _lootStrategy;

    public IReadOnlyList<ActivityChoice> LootStrategyChoices { get; }

    /// <summary>The list this run's kind loots by. Empty is a real answer and not a gap: a mission is not looted in
    /// these words at all, so it gets no list rather than the site list it never fitted (ET-174 AC-4).</summary>
    public IReadOnlyList<string> LootStrategies => Kind switch
    {
        ActivityKind.Abyssal => AbyssalLootStrategies,
        ActivityKind.Site => SiteLootStrategies,
        // A mission, and any kind this build has not met: no list at all.
        _ => []
    };

    /// <summary>Hidden rather than shown empty: a LOOT STRATEGY row with no buttons under it reads as a question
    /// the window failed to load, not as one that does not apply here.</summary>
    public bool IsLootStrategyShown => LootStrategyChoices.Count > 0;

    /// <summary>What the copied signature's own text said it was (ET-100) — the raw scan-window field, not
    /// anything the SDE could enrich it to. Always null in the abyss; a filament carries no signature.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureTypeText))]
    [NotifyPropertyChangedFor(nameof(HasSignature))]
    private string? _signatureGroup;

    /// <summary>The scan's own id, e.g. <c>RUS-326</c> — what tells one Sansha Refuge from the next one, which a
    /// site name cannot. Shown after the location, so the row names the site as well as the system.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationText))]
    private string? _signatureId;

    /// <summary>The signature's name once fully scanned — same field, same source as <see cref="SignatureGroup"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    [NotifyPropertyChangedFor(nameof(HasSignature))]
    [NotifyPropertyChangedFor(nameof(ClockHint))]
    private string? _signatureName;

    /// <summary>What the site catalogue carries under <see cref="SignatureName"/> (ET-80). Empty is the ordinary
    /// case rather than a fault — the match is on the English name only, so a miss cannot prove the site is absent —
    /// and so is more than one, since 218 catalogue names are shared by 613 dungeons. Nothing below ever picks one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    [NotifyPropertyChangedFor(nameof(ShipRestrictionText))]
    [NotifyPropertyChangedFor(nameof(HasShipRestriction))]
    private IReadOnlyList<SdeSite> _matchedSites = [];

    // Whether this window starts its run by itself once it has settled, instead of waiting for START. Set by the
    // clipboard signature offer (ET-158), which has no button to press.
    public bool StartsOnArrival { get; set; }

    // ── The six sections ────────────────────────────────────────────────────────────────────────────

    public ActivitySection Activity { get; } = new() { Title = "ACTIVITY", IsExpanded = true };

    /// <summary>One row per enemy type, on its own rather than inside ACTIVITY: a site's worth of rats pushed every
    /// other section under the fold (ET-115).</summary>
    public ActivitySection Enemies { get; } = new() { Title = "ENEMIES" };

    public ActivitySection Fit { get; } = new() { Title = "FIT" };

    public ActivitySection Fleet { get; } = new() { Title = "FLEET" };

    public ActivitySection Bounty { get; } = new() { Title = "BOUNTY" };

    public ActivitySection Loot { get; } = new() { Title = "LOOT" };

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

    public string ActingCharacterText => ActingCharacterName ?? "no character yet";

    /// <summary>The run's one fit (ET-107) — filled from ET-101's detection, or the reason it could not be. Never a
    /// proposal standing beside a choice: a manual pick comes back through the same reading as its own match reason.</summary>
    [ObservableProperty]
    private string _fitText = "no fit: the run has no character yet";

    /// <summary>Whether <see cref="FitText"/> names a fit. A state rather than a comparison against the text, so
    /// rewording a line can never silently flip what the window offers.</summary>
    [ObservableProperty] private bool _hasFit;

    [ObservableProperty] private string _fitVelocityText = "no max velocity";

    [ObservableProperty] private string _fitWarpSpeedText = "no warp speed";

    /// <summary>All six in window order — what the test walks to prove none of them is ever silent.</summary>
    public IReadOnlyList<ActivitySection> Sections { get; }

    // ── Weather and tier ────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ActivityChoice> WeatherChoices { get; }

    public IReadOnlyList<ActivityChoice> TierChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Weather))]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    [NotifyPropertyChangedFor(nameof(WeatherEnvironmentText))]
    [NotifyPropertyChangedFor(nameof(WeatherEffectText))]
    private int? _weatherIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(NeedsWeatherAndTier))]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    [NotifyPropertyChangedFor(nameof(TierText))]
    [NotifyPropertyChangedFor(nameof(WeatherEffectText))]
    private int? _tierIndex;

    public AbyssalWeather? Weather => WeatherIndex is { } index ? AbyssalWeather.All[index] : null;

    public bool HasWeatherAndTier => WeatherIndex is not null && TierIndex is not null;

    /// <summary>Drives the one chip in the header that asks for something. Only ever true for an abyssal run — a
    /// site has neither.</summary>
    public bool NeedsWeatherAndTier => IsAbyssal && !HasWeatherAndTier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickerShown))]
    private bool _isPickerOpen;

    /// <summary>Twelve buttons are worth the room while the question is open and in the way once it is answered, so
    /// the picker folds behind one line as soon as both halves are set.</summary>
    public bool IsPickerShown => !HasWeatherAndTier || IsPickerOpen;

    // ── The clock ───────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _clockLabel = string.Empty;

    [ObservableProperty] private string _clockText = NoClock;

    [ObservableProperty] private bool _isClockWarning;

    [ObservableProperty] private bool _isClockCritical;

    [ObservableProperty] private string _startText = string.Empty;

    [ObservableProperty] private string _endText = string.Empty;

    // ── Correcting the clock after the fact ─────────────────────────────────────────────────────────
    // Manual start and stop are the only source a site run has — there is no site-entry or site-exit line in the
    // gamelog to fall back on — so the human slack is part of the measurement: you press START once the fight is
    // already going, and STOP once the loot is already in the hold. Without a correction every stored duration is
    // systematically off, which is why this is part of the run and not a convenience.

    /// <summary>The start as the pilot corrected it, or null while the measured one still stands. Held beside
    /// <see cref="AnchorUtc"/> rather than over it: the measured moment is what the fleet envelope is made of, and
    /// overwriting it would lose the difference the window exists to show.</summary>
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

    /// <summary>What a kind this build has never heard of is called. A run stored by a later version still opens
    /// and still reads as a run — the window going down over a header is the failure mode AGENTS.md §2 forbids.
    /// No kind that ships may land here, and a test walks every <see cref="ActivityKind"/> to say so (ET-174 AC-1):
    /// adding one without deciding its title turns that test red.</summary>
    public const string UnknownKindHeader = "RUN";

    public string HeaderTitle => Kind switch
    {
        ActivityKind.Abyssal => "ABYSSAL RUN",
        ActivityKind.Site => "SITE RUN",
        ActivityKind.Mission => "MISSION RUN",
        _ => UnknownKindHeader
    };

    // Start, stop and discard steer the run for everybody in it, so all three hang off the same authority (AC-4).
    // Start and stop are the same slot seen from two sides and never both apply: a run that is going can only be
    // stopped, and offering to re-start it over itself is what put START next to a ticking clock.
    /// <summary>Hidden while a copied site is waiting: the only two answers there are SAVE and DISCARD, and START
    /// would pick the run being waited on back up.</summary>
    public bool IsStartButtonVisible =>
        Authority.CanControl && RunState != ActivityRunState.Running && _pendingSignature is null;

    public bool IsStopButtonVisible => Authority.CanControl && RunState == ActivityRunState.Running;

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

    public string CommandStatusText => Authority.StatusText;

    /// <summary>
    /// Why this run has no fleet while the pilot plainly has several. Only when both halves are true: several
    /// fleets in play <i>and</i> no fleet id came out of it — with a fleet settled there is nothing to report, and
    /// with one fleet there was never a question.
    /// </summary>
    public bool HasFleetNotice => FleetsInPlay > 1 && FleetId is null;

    /// <summary>Says which way the run went and what would settle it. Not a warning about a fault: two started
    /// fleets is a legitimate state, and the window's job is to make the consequence visible rather than to refuse
    /// it.</summary>
    public string FleetNoticeText =>
        $"You are in {FleetsInPlay} started fleets at once, so this run belongs to none of them and is not shared. "
        + "Conclude the ones you are not flying to file it under one.";

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

    /// <summary>Shown over every payout figure. The window reports an expectation, and never implies EVE's own
    /// payout rule follows our exclusions.</summary>
    public string PayoutExpectationLabel => RunPayoutSplit.ExpectationLabel;

    /// <summary>Who this window has actually heard from, one row per member that sent a location sample. Never a
    /// roster: nothing here can see a member who is not sharing their location, which is what
    /// <see cref="FleetBasisText"/> is on screen to say.</summary>
    public ObservableCollection<ActivityFleetMemberViewModel> FleetMembers { get; } = [];

    /// <summary>What the figures were counted over. "sharing a location" read as "they are in the same place", which
    /// is the very question a pilot asks this chip — under it stood RaymondKrah in Amarr and Jithran in Shaggoth
    /// (Raymond, 2026-09-03). Each member shares theirs, and that is all this counts.</summary>
    public string FleetStatusText => FleetMemberCount > 1
        ? IsAbyssal
            ? $"based on {AnchoredFleetMemberCount} of {FleetMemberCount} members sharing their location"
            : $"based on {FleetMemberCount} members sharing their location"
        : "no other member has reported in yet";

    /// <summary>
    /// What the count is counted from, said outright. <see cref="FleetMemberCount"/> counts location samples, so a
    /// member who does not share their location is missing from both the number and the list — and a list of two
    /// names in a fleet of three is a lie unless it says what it is a list of.
    /// </summary>
    public string FleetBasisText => FleetMembers.Count == 0
        ? "No member has shared anything yet, so there is nobody to list."
        : "Counted from what members share. A member sharing nothing is in the fleet but not in this list.";

    /// <summary>
    /// What the rows above add up to, and only them. That is why it stands between the names and
    /// <see cref="FleetBasisText"/>: a total that covered more than the rows it sits under would need explaining,
    /// and the caption below is already the line that says the fleet may be larger than this list. A member sharing
    /// neither figure is in neither the rows nor the sum, which is the same rule in both places.
    ///
    /// Never a zero for a figure nobody offered — the two halves are counted apart, so a fleet sharing bounty and no
    /// loot says exactly that rather than reporting nothing looted.
    /// </summary>
    public string FleetTotalText => (_FleetSum(row => row.LootIsk), _FleetSum(row => row.BountyIsk)) switch
    {
        (null, null) => "no member is sharing loot or bounty",
        ({ } loot, null) => $"loot {ActivityFleetMemberViewModel.Isk(loot)} · bounty not shared",
        (null, { } bounty) => $"loot not shared · bounty {ActivityFleetMemberViewModel.Isk(bounty)}",
        ({ } loot, { } bounty) =>
            $"loot {ActivityFleetMemberViewModel.Isk(loot)} · bounty {ActivityFleetMemberViewModel.Isk(bounty)}"
    };

    public bool IsFleetTotalShown => FleetMembers.Count > 0;

    private decimal? _FleetSum(Func<ActivityFleetMemberViewModel, decimal?> figure) =>
        FleetMembers.Select(figure).OfType<decimal>().ToList() is { Count: > 0 } shared ? shared.Sum() : null;

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
        _ => _isManualRun ? "manual" : "estimated from fleet"
    };

    /// <summary>
    /// What the clock does not say on its face. <c>AbyssalSpace.Describe</c> writes a "+" for this; here it is a
    /// sentence under the figure instead, which is where it ended up after the first round of review.
    /// </summary>
    public string ClockHint => IsAbyssal
        ? (FleetMemberCount > 1 ? $"{FleetStatusText}: the envelope is the earliest anchored run. " : string.Empty)
          + "The clock is a floor — the moment of entry cannot be observed, so this is at most what is left."
        : _pendingSignature is { } waiting
            ? $"{waiting.Name} is copied and waiting. Save or discard this {SignatureName} run and it takes over."
            : RunState == ActivityRunState.Stopped
                // STOP is a pause, not an end (Raymond, 2026-09-02): stepping out mid-site and coming back has to
                // cost you nothing, so START picks the same run back up. What ends a run is SAVE or DISCARD.
                ? "Stopped runs keep their figures; start picks this run back up."
                : "Manual start and stop are the only source for this run.";

    // ── Section bodies ──────────────────────────────────────────────────────────────────────────────

    public bool IsInsideAbyssal => InsideAbyssal ?? IsAbyssal;

    public string LocationText => IsInsideAbyssal
        ? "none — an abyssal pocket has no location"
        : (LocationDisplay ?? SolarSystem) is { } place
            // Never behind "not known yet": that line is about us rather than about where he is, and a scan id in
            // brackets after it would read as half a place.
            ? SignatureId is { Length: > 0 } signature ? $"{place} ({signature})" : place
            : "not known yet";

    /// <summary>Shown only once there is a system to show. "not known yet" is a line about us, not about where he
    /// is, and the row is hidden instead.</summary>
    public bool IsLocationShown => IsInsideAbyssal || LocationDisplay is not null || SolarSystem is not null;

    public string BountyText => IsInsideAbyssal
        ? "— no bounty in abyssal space"
        : BountyIsk > 0 ? $"{BountyIsk:N0} ISK — own character" : "no payouts yet — own character";

    /// <summary>Whether there is a copied signature behind this run at all. A run started by hand has none, and a
    /// row that can only ever read "not known yet" is worse than no row.</summary>
    public bool HasSignature => SignatureGroup is not null || SignatureName is not null;

    public string SignatureTypeText => SignatureGroup ?? "not known yet";

    /// <summary>The site, described by what every catalogue match agrees it is — archetype, faction, DED, whether
    /// it turns you away at the gate. Silent about anything they disagree on, and silent about the catalogue
    /// itself: how many rows happen to share an English name is our problem, not the reader's.</summary>
    public string SignatureSiteText => SignatureName is not { } name
        ? "not known yet"
        : SdeSiteDescription.DescribeCommon(MatchedSites) is { Length: > 0 } common
            ? $"{name} — {common}"
            : name;

    /// <summary>The hulls the site lets in, when every match names the same ones — the one fact here that can turn
    /// you away at the gate, so it is stated before you warp rather than discovered after. Null when there is
    /// nothing to add over <see cref="SignatureSiteText"/>, which already carries "ship-restricted" itself.</summary>
    public string? ShipRestrictionText =>
        MatchedSites.Select(_ShipRule).Distinct().ToList() is [{ } only] ? only : null;

    public bool HasShipRestriction => ShipRestrictionText is not null;

    /// <summary>
    /// The hull list behind the site line, on demand. It used to stand inline in the ACTIVITY section, where a site
    /// like Blood Lookout ran to thirty-odd names over five lines and pushed LOCATION and LOOT STRATEGY off the
    /// bottom (Raymond, 2026-09-02). The site line still says <c>ship-restricted</c> itself, so the fact of the
    /// restriction never depended on this list being visible.
    ///
    /// Shown through <see cref="IDialogService.ShowMessageAsync"/>, the same plain dialog the fit picker falls back
    /// on — no new surface for one list.
    /// </summary>
    [RelayCommand]
    private async Task ShowShipRestrictionAsync()
    {
        if (ShipRestrictionText is not { } hulls)
            return;

        await _services.GetRequiredService<IDialogService>()
            .ShowMessageAsync("Ships allowed at this site", hulls);
    }

    public string TierText => TierIndex is { } tier
        ? $"{Tiers[tier]} (Tier {tier})"
        : "not set";

    public string WeatherEnvironmentText => Weather?.EnvironmentName ?? "not set";

    public string WeatherEffectText => Weather is { } weather && TierIndex is { } tier
        ? $"{weather.Bonus} · {_PenaltyRange(tier)} {weather.PenaltyTarget}"
        : "not set";

    /// <summary>
    /// The caption over the ISK figures in the loot section. It names its own source on purpose, and it used to name
    /// the wrong one: it read "Prices are the clipboard column as it stood at the copy" long after the ISK in a
    /// copied line stopped being held at all. LOOT, CONSUMED and NET are valued on type id out of the price cache
    /// EVE Together refreshes hourly, which is the rule for every figure in this app.
    ///
    /// The per-line figure under this caption is the copied column, and it is the only thing here that is: naming
    /// both is the difference between a total a pilot can act on and one whose source he has to guess. Which cache
    /// snapshot valued them is <see cref="RunLootViewModel.TotalIskLabel"/>'s to say, and it says it beside the
    /// total rather than twice.
    /// </summary>
    public string IskLabel =>
        "Prices come from EVE Together's own hourly price lookup on type id. The figure beside each line is the "
        + "copied column, and is not what these add up.";

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Restore the remembered weather and tier, find out whose run this is, and attach to the run that is
    /// already running if there is one. Separate from the constructor so the window can be built synchronously and a
    /// test can assert the round-trip without racing anything.</summary>
    public async Task LoadAsync()
    {
        // Same guard _AdoptRunningRunAsync already carries: with no dispatcher there is nothing remembered to
        // restore, and that is a window without a store rather than a fault.
        if (_services.GetService<CqrsDispatcher>() is not null)
        {
            using var scope = _services.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Query(new GetSettingsQuery());

            WeatherIndex = _Restore(settings.FirstOrDefault(s => s.Key == WeatherSettingKey)?.Value,
                AbyssalWeather.All.Count);
            TierIndex = _Restore(settings.FirstOrDefault(s => s.Key == TierSettingKey)?.Value, Tiers.Count);

            // A remembered strategy from the other kind of run addresses nothing here, so it reads as unset — the same
            // rule the two indices get.
            string? strategy = settings.FirstOrDefault(s => s.Key == LootStrategySettingKey)?.Value;
            LootStrategy = LootStrategies.Contains(strategy) ? strategy : null;
        }

        _SyncChoices();
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
            int? picked = await dialogs.PickCharacterAsync("Whose run is this?",
                [.. candidates.Select(character => new CharacterPickOption(
                    character.EsiCharacterId!.Value, character.Name, "local character", Enabled: true))]);
            chosen = candidates.FirstOrDefault(character => character.EsiCharacterId == picked);
        }

        if (chosen is null)
            return false;

        _runCharacterId = chosen.EsiCharacterId;
        _runCharacterName = chosen.Name;
        return true;
    }

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
        Result<RunningRunDto> running = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Query(new GetRunningRunQuery());
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

            _pendingSignature = (SignatureId, SignatureGroup, copied, MatchedSites);
            SignatureId = run.Signature;
            SignatureGroup = null;
            SignatureName = run.SiteName;
            MatchedSites = [];
            _SignatureDecision("the run left open belongs to a group, so it waits", copied);
        }

        RunId = run.Id;
        AnchorUtc = run.StartedAtUtc;
        StoppedAtUtc = null;
        await _JoinStoredRunToFleetGroupAsync(scope, run);
        GroupCode ??= run.GroupCode;
        _isManualRun = true;
        RunState = ActivityRunState.Running;
        await _AdoptCharacterAsync(checked((int)run.CharacterId));
        _StartEnemyObservations();

        // After the state above: StopRun only acts on a run it considers running.
        if (_pendingSignature is not null)
            StopRun(DateTime.UtcNow);

        return true;
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
        if (_pendingSignature is not null)
            return;

        if (GroupCode is not { } fleetGroupCode || string.Equals(run.GroupCode, fleetGroupCode, StringComparison.Ordinal))
            return;

        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        if (run.GroupCode is not null)
            await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(run.Id));
        await dispatcher.Send(new LinkRunToGroupCodeCommand(run.Id, fleetGroupCode));
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
    /// </summary>
    public void JoinFleetRun(RunGroupCodeStart start)
    {
        GroupCode = start.GroupCode;
        FleetId = start.FleetId;
        AnchorUtc = start.StartedAtUtc;
        StoppedAtUtc = null;
        // The commander's scan id names the same signature on this member's own scanner — the id belongs to the
        // system, not to the pilot (ET-151) — so LOCATION reads RUS-326 · Shousran here too instead of the bare
        // system it showed a member while the commander had the site.
        SignatureId ??= start.Signature;
        RunState = ActivityRunState.Running;
        _StartEnemyObservations();
        // A joined run still needs its own row, or this member's loot and bounties have nothing to hang off.
        _ = _BeginEstimatedRunAsync(start.StartedAtUtc, start.SiteName);
        Refresh(DateTime.UtcNow);
    }

    /// <summary>The commander started, and this window was already open on nothing. Only a start that came from the
    /// commander joins a window: a member's own start is announced too, and it is not an invitation.</summary>
    private void _OnFleetRunStarted(FleetRunGroupCodeEvent integrationEvent)
    {
        RunGroupCodeStart start = integrationEvent.Data;
        if (!start.IsFleetCommander || RunState != ActivityRunState.NotStarted
            || (FleetId is { } fleetId && fleetId != start.FleetId) || start.ActivityKind != Kind)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => JoinFleetRun(start));
    }

    private void _OnFleetRunStopped(FleetRunStoppedEvent integrationEvent)
    {
        RunGroupStop stop = integrationEvent.Data;
        if (GroupCode is not { } groupCode || !string.Equals(groupCode, stop.GroupCode, StringComparison.Ordinal))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => StopRun(stop.StoppedAtUtc));
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
            StoppedAtUtc ??= discard.DiscardedAtUtc;
            RunState = ActivityRunState.Discarded;
            _EndEnemyObservations();
            GroupCode = null;
            RunNoticeText = "The fleet commander discarded this run, so it is no longer part of the group. "
                            + "Nothing you already saved is gone. Close this window when you have read it.";
            if (RunLoot is not null)
                _ = RunLoot.RefreshAsync();
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
        _RefreshRunCharacters();

        if (_services.GetService<ICharacterPortraitProvider>() is not { } portraits)
            return;

        foreach (RunCharacterRowViewModel row in RunCharacters)
            await row.LoadPortraitAsync(portraits);
    }

    /// <summary>
    /// Put this window's state onto the column. Only the acting character can be restless: this window holds the
    /// one run there is until ET-130 lets a second exist, so red and amber are its own clock rather than something
    /// each row could work out for itself.
    /// </summary>
    private void _RefreshRunCharacters()
    {
        if (RunCharacters.Count == 0)
            return;

        int? acting = _ActingCharacterId();
        foreach (RunCharacterRowViewModel row in RunCharacters)
        {
            row.IsSelected = row.IsEsiLinked && row.CharacterId == acting;
            row.HasRunningRun = row.IsSelected && RunState is ActivityRunState.Running;
            row.Attention = (row.HasRunningRun, IsClockCritical, IsClockWarning) switch
            {
                (true, true, _) => RunCharacterAttention.Critical,
                (true, _, true) => RunCharacterAttention.Warning,
                _ => RunCharacterAttention.None
            };
        }

        // The refusal changes with the run's state, and the clock is the only thing that moves that state here.
        SelectRunCharacterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Switch the window to this character. Refused while a run is on the clock — moving the window off a running
    /// run would leave it filed under a pilot who is no longer looking at it — and refused for an unlinked
    /// character, who has no id to file anything under.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectRunCharacter))]
    private void SelectRunCharacter(RunCharacterRowViewModel row)
    {
        _runCharacterId = row.CharacterId;
        _runCharacterName = row.Name;
        _namedCharacterId = null;
        _ = _RefreshActingCharacterAsync();
        _RefreshRunCharacters();
    }

    private bool CanSelectRunCharacter(RunCharacterRowViewModel? row) =>
        row is { IsEsiLinked: true } && RunState is not ActivityRunState.Running;

    /// <summary>Fill the run's fit from ET-101's reading. Clock-driven like the fleet command is, so starting a run
    /// fills it without the player confirming anything. An unlinked fit comes back through the same reading, so it
    /// survives this window being closed and reopened mid-run.</summary>
    public async Task RefreshFitAsync()
    {
        if (_ActingCharacterId() is not { } characterId
            || _services.GetService<IShipFitDetectionService>() is not { } detection)
            return;

        ShipFitDetectionReading reading = detection.GetReading(characterId);
        if (ReferenceEquals(reading, _fitReading))
            return;

        _fitReading = reading;
        ApplyFitDetection(reading);
        await _LoadFitStatsAsync(reading.SelectedFit?.Id);
    }

    private async Task _LoadFitStatsAsync(int? fittingId)
    {
        if (fittingId is not { } id || _services.GetService<IFittingRepository>() is not { } fittings)
        {
            ApplyFitStats(null, fitCouldBeRead: true);
            return;
        }

        LocalFitting? fitting = await fittings.FindByIdAsync(id);
        EsiFitting? esi = _ReadFitting(fitting?.RawJson);
        ApplyFitStats(
            esi is null ? null : await _services.GetRequiredService<IFitStatsProvider>().ComputeAsync(esi),
            fitCouldBeRead: esi is not null);
    }

    private static EsiFitting? _ReadFitting(string? rawJson)
    {
        if (rawJson is null)
            return null;
        try { return JsonSerializer.Deserialize<EsiFitting>(rawJson); }
        catch (JsonException) { return null; }
    }

    [RelayCommand]
    private async Task ChooseFitAsync()
    {
        var dialogs = _services.GetRequiredService<IDialogService>();
        if (_ActingCharacterId() is not { } characterId)
        {
            await dialogs.ShowMessageAsync("Choose a fit",
                "Start the run first — its character is what a fit is filed under.");
            return;
        }

        var picker = new FitPickerViewModel(_services, FitPickerMode.Single, alreadyAdded: null,
            composition: null, currentFitHash: null, skillCheckCharacterId: characterId);
        FitReferenceInfo? fit = await dialogs.PickFitAsync(picker);
        if (fit is null)
            return;
        if (fit.LocalFittingId is null)
        {
            await dialogs.ShowMessageAsync("Choose a local fit", "Only a local fit can be filed against a run.");
            return;
        }

        var detection = _services.GetRequiredService<IShipFitDetectionService>();
        Result overrideResult = await detection.SetManualFitAsync(characterId, fit.LocalFittingId);
        if (!overrideResult.IsSuccess)
        {
            await dialogs.ShowMessageAsync("Fit selection",
                overrideResult.Messages.FirstOrDefault()?.Text ?? "Could not save the fit selection.");
            return;
        }

        _fitReading = null;
        await RefreshFitAsync();
    }

    /// <summary>Unlink: the run goes on without a fit. Stored by the detection service rather than held here, or
    /// closing and reopening the window mid-run would quietly fill back in what the player just took off.</summary>
    [RelayCommand]
    private async Task DetachFitAsync()
    {
        if (_ActingCharacterId() is not { } characterId
            || _services.GetService<IShipFitDetectionService>() is not { } detection)
            return;

        Result detached = await detection.DetachFitAsync(characterId);
        if (!detached.IsSuccess)
        {
            await _services.GetRequiredService<IDialogService>().ShowMessageAsync("Fit selection",
                detached.Messages.FirstOrDefault()?.Text ?? "Could not unlink the fit.");
            return;
        }

        _fitReading = null;
        await RefreshFitAsync();
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
        _RefreshLocation(nowUtc);
        _RefreshClock(nowUtc);
        _RefreshSummaries();
        _ = RefreshFleetCommandAsync(nowUtc);
        _ = RefreshFitAsync();
        _ = _RefreshActingCharacterAsync();
        _RefreshRunCharacters();
        _ShareRunLootWithFleet();
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

    [RelayCommand]
    private async Task SelectWeatherAsync(int index)
    {
        WeatherIndex = index;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, index.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private async Task SelectTierAsync(int index)
    {
        TierIndex = index;
        _AfterChoice();
        await _PersistAsync(TierSettingKey, index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Pressing the strategy that is already set clears it — the row has no other way back to unset, and
    /// a wrong label on a saved run is worse than none.</summary>
    [RelayCommand]
    private async Task SelectLootStrategyAsync(int index)
    {
        LootStrategy = LootStrategy == LootStrategies[index] ? null : LootStrategies[index];
        _SyncChoices();
        Refresh(DateTime.UtcNow);
        await _PersistAsync(LootStrategySettingKey, LootStrategy ?? string.Empty);
    }

    [RelayCommand]
    private async Task ClearWeatherAndTierAsync()
    {
        WeatherIndex = null;
        TierIndex = null;
        _AfterChoice();
        await _PersistAsync(WeatherSettingKey, string.Empty);
        await _PersistAsync(TierSettingKey, string.Empty);
    }

    /// <summary>Reopen the picker on the run that is already answered — the one line it folded behind.</summary>
    [RelayCommand]
    private void OpenPicker() => IsPickerOpen = true;

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
            await _SetStoredRunStoppedAsync(null);
            StoppedAtUtc = null;
            CorrectedStopUtc = null;
            RunState = ActivityRunState.Running;
            _StartEnemyObservations();
            if (RunLoot is not null)
                await RunLoot.RefreshAsync();
            Refresh(nowUtc);
            return;
        }

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

        StartManualRun(nowUtc);
        await _StoreRunAsync(nowUtc);
    }

    [RelayCommand]
    private void StopRun() => StopRun(DateTime.UtcNow);

    /// <summary>Move the window to a running run on the clock. <see cref="StartRunAsync"/> is what also gives it a
    /// row in the store; the fleet envelope calls this too, for a run nobody pressed a button for.</summary>
    public void StartManualRun(DateTime nowUtc)
    {
        AnchorUtc = nowUtc;
        StoppedAtUtc = null;
        // A stopped manual result is final; a later ESI anchor cannot reopen it.
        _isManualRun = true;
        _bounties.Clear();
        BountyIsk = 0;
        RunState = ActivityRunState.Running;
        _StartEnemyObservations();
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

        using var scope = _services.CreateScope();
        Result<Guid> started = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(
            new StartRunCommand(characterId, Kind, startedAtUtc,
                // No type id: a signature names a dungeon, and the catalogue's DungeonId is not the type id this
                // column holds. The name travels instead.
                SiteTypeId: 0,
                SiteName: SignatureName,
                SolarSystemId: null,
                GroupCode: GroupCode,
                Signature: SignatureId,
                FleetId: FleetId,
                // The one thing that turns a site start into a shared run. Not CanControl: a member steering their
                // own run may control it without commanding anybody, and answering "am I the boss" is the
                // authority's own job rather than something reassembled here (ET-147, ET-152).
                IsFleetCommander: Authority.IsFleetCommander,
                SolarSystemName: SolarSystem,
                // This window's own start button is the clipboard/signature path — the site comes from what the
                // pilot pasted, not from a catalogue pick (ET-163).
                Origin: EveUtils.Shared.Modules.Runs.Enums.RunOrigin.Clipboard));
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
        // reached a single other member (Raymond, 2026-09-03).
        if (GroupCode is null)
            GroupCode = (await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Query(new GetRunningRunQuery())).Value?.GroupCode;

        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(DateTime.UtcNow);
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
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetRunStoppedCommand(runId, stoppedAtUtc));
    }

    /// <summary>
    /// Bring every member's clock to rest at the commander's moment. There was no such announcement at all until
    /// now — START and DISCARD crossed the wire and STOP simply did not exist — so a member watched a run that had
    /// been over for minutes carry on counting (Raymond, 2026-09-03).
    ///
    /// Only from the window that commands the run: a member stopping their own leg ends nobody else's, and the
    /// event is received back here too, where <see cref="StopRun"/>'s own guard makes the second one a no-op.
    /// </summary>
    private void _AnnounceStopToFleet(DateTime stoppedAtUtc)
    {
        if (!Authority.CanControl || FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _ = eventBus.PublishAsync(
            new FleetRunStoppedEvent(new RunGroupStop(fleetId, Kind, groupCode, stoppedAtUtc)),
            EventTarget.Both);
    }

    /// <summary>
    /// Take the two typed times as this run's own. Refused rather than straightened out when they cannot be true:
    /// an end before its start is not a duration, and an abyssal run longer than <c>AbyssalSpace.RunLimit</c> is a
    /// run whose pilot was dead before it ended.
    ///
    /// It moves this run's row and nothing else. A group's envelope hangs on the earliest start over the whole
    /// fleet, taken from the samples in <see cref="ApplyFleetEnvelope"/> — correcting your own clock does not
    /// re-anchor anybody, which is why <see cref="AnchorUtc"/> is left standing as the measured moment.
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

        if (IsAbyssal && end - start > AbyssalSpace.RunLimit)
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
    /// (ET-152). Nothing is awaited here any more, so the answer is on screen the moment the window opens.
    /// </summary>
    public Task RefreshFleetCommandAsync(DateTime nowUtc)
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
        return Task.CompletedTask;
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

        DateTime nowUtc = DateTime.UtcNow;
        using var scope = _services.CreateScope();
        Result result = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SaveRunCommand(
            runId, EffectiveStopUtc ?? nowUtc, nowUtc, [], _bounties, _enemyObservations?.ToInputs() ?? [], [],
            // Null leaves the row's own start alone; only a hand-corrected start travels.
            CorrectedStartUtc,
            IsTimeCorrected ? nowUtc : null));
        if (!result.IsSuccess)
        {
            RunNoticeText = result.Messages.FirstOrDefault()?.Text ?? "Could not save this run.";
            _services.GetService<IToastService>()?.Show("Run not saved", RunNoticeText, ToastKind.Error);
            return;
        }

        RunNoticeText = null;
        RunState = ActivityRunState.Saved;
        _EndEnemyObservations();
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(nowUtc);
        // Only here, and only for this window: the run is committed and there is nothing left to do to it. A failed
        // save falls out above with the reason still on screen, and a group's other members keep their own windows —
        // saving is each member's own, and only the FC's DISCARD reaches anybody else (ET-105).
        CloseRequested?.Invoke();
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
    /// End the shared run for everyone in it. Confirmed first, because it reaches every other member's machine —
    /// and it still takes nothing from them: a member who already saved keeps their run, unlinked from the group
    /// (ET-105 AC-1).
    /// </summary>
    [RelayCommand]
    private async Task DiscardRunAsync()
    {
        if (!Authority.CanControl || RunId is not { } runId)
            return;

        var dialogs = _services.GetRequiredService<IDialogService>();
        if (!await dialogs.ConfirmAsync("Discard this run?",
                "This ends the run for every member of the fleet. Nobody loses what they already saved — their run "
                + "stays, on its own, no longer part of this group.", "Discard"))
            return;

        DateTime nowUtc = DateTime.UtcNow;
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        Result discarded = await dispatcher.Send(new DiscardRunCommand(runId, nowUtc));
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

        // Thrown away means this window is done, so it closes (ET-155). It used to be cleaned out and left standing
        // ready for the next START, which is the very shape in which old run state kept coming back. Only here: a
        // refused command and a cancelled confirmation both fall out above with the window still on its run.
        _SendPendingSignatureToANewWindow();
        GroupCode = null;   // the group ended with the run, which is what a discard reaches the other members to say.
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Where the site copied during the run ends up now that the window closes instead of clearing itself. Not a new
    /// route: a signature copied with no run window open opens a fresh window on it, and that is exactly what this
    /// hands to <see cref="IDialogService.ShowActivityWindow"/> — so the copy is answered the way every other copy
    /// is, rather than evaporating with the window that was holding it (ET-155).
    /// </summary>
    private void _SendPendingSignatureToANewWindow()
    {
        if (_pendingSignature is not { } pending || _services.GetService<IDialogService>() is not { } dialogs)
            return;

        _pendingSignature = null;
        dialogs.ShowActivityWindow(new ActivityWindowViewModel(Kind, _services)
        {
            SignatureId = pending.Id,
            SignatureGroup = pending.Group,
            SignatureName = pending.Name,
            MatchedSites = pending.Sites
        });
    }

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
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new DiscardRunCommand(runId, DateTime.UtcNow));
        return true;
    }

    /// <summary>
    /// Everything the last run left standing on this window, in one place. A new run starts clean (Raymond,
    /// 2026-09-02): what survives a run survives because it was decided to, not because nobody cleared it, and one
    /// method rather than three copies is the whole point — three drift apart the first time a field is added, which
    /// is how this gap opened.
    ///
    /// START is deliberately not one of its callers: STOP is a pause, so pressing START again picks the same run
    /// back up. Ending a run closes the window now — SAVE and DISCARD both do (ET-155) — so what is left here is the
    /// one case where the window stays and the run does not: a copied site taking over from a run just closed out.
    ///
    /// Kept on purpose, and each for its own reason: the weather, the tier and the loot strategy, because you fly the
    /// same ones several runs in a row — which is what their settings keys exist for; the signature and its matched
    /// sites, because they are what the window was opened on rather than anything the run produced; and whose run it
    /// is, because that is the client's pilot and not this run's property. The fleet's members are not run state
    /// either — they are whoever is heard from right now, and stop being listed on their own when they go quiet.
    /// </summary>
    private void _ResetForNewRun()
    {
        RunId = null;
        AnchorUtc = null;
        StoppedAtUtc = null;
        CorrectedStartUtc = null;
        CorrectedStopUtc = null;
        TimeCorrectionError = null;
        RunState = ActivityRunState.NotStarted;
        RunNoticeText = null;
        // Otherwise a discarded manual run left the window refusing every later fleet anchor: the flag that makes a
        // stopped manual result final outlived the result it was final about.
        _isManualRun = false;
        _bounties.Clear();
        BountyIsk = 0;
        TotalLootIsk = null;
        Participants.Clear();
        // ET-101 reads the ship again for the new run rather than leaving the last one's fit on screen; the reading
        // itself lives in the detection service, so this only drops what this window cached of it.
        _fitReading = null;
        _EndEnemyObservations();
    }

    /// <summary>
    /// A clipboard copy has just been filed against a run. Refreshed only when it is <i>this</i> window's run, so a
    /// second window on another run does not redraw for loot that is not its own.
    ///
    /// The window reads its loot from the store, and until this arrived nothing told it to read again: a copy taken
    /// while the window stood open was stored, toasted as "Loot copied", and left the LOOT section under it still
    /// reading "no loot captured" (Raymond, 2026-09-02). The run had the loot; the window simply never looked.
    /// </summary>
    private void _OnRunLootCaptured(RunLootCapturedEvent integrationEvent)
    {
        if (RunLoot is null || integrationEvent.Data != RunId)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RunLoot.RefreshAsync());
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

        if (!IsAbyssal)
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

        if (RunState is not (ActivityRunState.Stopped or ActivityRunState.Saved) && !_isManualRun && anchors.Count > 0)
        {
            AnchorUtc = anchors.Min();
            StoppedAtUtc = null;
            RunState = ActivityRunState.Running;
            _StartEnemyObservations();
            // A run nobody pressed START for still needs its row, or the loot has nothing to attach to.
            if (RunId is null)
                _ = _BeginEstimatedRunAsync(AnchorUtc.Value);
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
        OnPropertyChanged(nameof(FleetBasisText));
        OnPropertyChanged(nameof(FleetTotalText));
        OnPropertyChanged(nameof(IsFleetTotalShown));
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
        if (await _AdoptRunningRunAsync() || !await _ResolveCharacterAsync(mayAsk: false))
            return;

        SignatureName ??= siteName;
        await _StoreRunAsync(anchorUtc);
    }

    /// <summary>
    /// Every bounty line this pilot's gamelog writes while the run is going, at the line's own time — the run's
    /// <c>RunBountyEntry</c> rows, which SAVE hands straight to the store. Filtered by character because the section
    /// says "own character" and this client watches every pilot's log at once.
    /// </summary>
    private void _OnBountyObserved(string characterName, BountyEvent bounty)
    {
        if (RunState != ActivityRunState.Running || IsInsideAbyssal
            || !string.Equals(characterName, _runCharacterName, StringComparison.OrdinalIgnoreCase))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _bounties.Add(new RunBountyEntryInput { OccurredAtUtc = bounty.Timestamp, Isk = bounty.Isk });
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
        if (_gamelog is null || _runCharacterName is null)
            return;

        CharacterMetricsSnapshot snapshot = _gamelog.Snapshot(_runCharacterName);
        if (snapshot.AbyssalAnchor is not null)
            InsideAbyssal = true;
        else if (snapshot.Location is not null && snapshot.LocationUnavailableReason is null)
            InsideAbyssal = false;

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

    public void Dispose()
    {
        if (_gamelog is not null)
        {
            _gamelog.CombatObserved -= _OnCombatObserved;
            _gamelog.BountyObserved -= _OnBountyObserved;
        }

        _metricSubscription?.Dispose();
        _lootSubscription?.Dispose();
        _fleetRunStartedSubscription?.Dispose();
        _fleetRunStoppedSubscription?.Dispose();
        _fleetRunDiscardedSubscription?.Dispose();
        _timer?.Stop();
        _timer = null;
    }

    // The event fires for damage either way — "250 to Centii Scavenger" and "1 from Centii Servant" alike — and both
    // are the same kind of enemy, so the direction is dropped here rather than carried into the list (ET-115).
    private void _OnCombatObserved(int characterId, string target, DateTime observedAtUtc, DamageDirection direction)
    {
        if (RunState != ActivityRunState.Running)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _enemyObservations?.Record(characterId, target, observedAtUtc));
    }

    private void _StartEnemyObservations()
    {
        if (_enemyObservations is not null)
            return;

        ISdeAccessor? sde = _services.GetService<ISdeAccessor>();
        _enemyObservations = sde is null || _runCharacterId is null ? null
            : new RunEnemyObservationCollector(_runCharacterId.Value,
                name => sde.TryGetTypeId(name, out int typeId) ? typeId : null);
        if (_enemyObservations is not null)
            // Only the summary: re-announcing the list itself while a count is being typed would rebind the editor
            // under the cursor. The rows are an ObservableCollection — the list keeps itself up to date.
            _enemyObservations.Changed += _RefreshSummaries;
        OnPropertyChanged(nameof(EnemyObservations));
        _RefreshSummaries();
    }

    /// <summary>Let go of the list, once the run it belongs to is committed or thrown away.</summary>
    private void _EndEnemyObservations()
    {
        if (_enemyObservations is not null)
            _enemyObservations.Changed -= _RefreshSummaries;

        _enemyObservations = null;
        OnPropertyChanged(nameof(EnemyObservations));
        _RefreshSummaries();
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────────────

    private void _RefreshClock(DateTime nowUtc)
    {
        DateTime effectiveNow = EffectiveStopUtc ?? nowUtc;
        ClockLabel = IsAbyssal
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

        if (!IsAbyssal)
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

    // The signature arrives after construction, from the object initialiser the toast opens the window with — so the
    // shut ACTIVITY header has to be worked out again then, not only on the next clock tick.
    partial void OnSignatureNameChanged(string? value) => _RefreshSummaries();

    partial void OnMatchedSitesChanged(IReadOnlyList<SdeSite> value) => _RefreshSummaries();

    private void _RefreshSummaries()
    {
        Activity.HeaderSummary = _ActivitySummary();
        Enemies.HeaderSummary = _EnemySummary();
        Fit.HeaderSummary = FitText;
        // Never "solo": nothing here can observe the absence of a fleet, only the presence of one. Without any the
        // section is hidden (IsFleetShown) and this line is not on screen at all.
        Fleet.HeaderSummary = FleetMemberCount > 1 ? FleetStatusText : "no fleet has reported in";
        Bounty.HeaderSummary = BountyText;
        // What was collected first, the value as an aside. A shut section that only reported "no price" read as a
        // fault while two items sat in it (Raymond, 2026-09-02).
        Loot.HeaderSummary = RunLoot?.Captures.Count > 0
            ? $"{_LootItemCount()} · {RunLoot.NetIskDisplay}"
            : RunLoot?.RunStatusMessage ?? "no loot captured";
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
            _pendingSignature = null;
            _SetSignature(id, group, name, sites);
            _SignatureDecision("no run of another site was open", name);
            Refresh(DateTime.UtcNow);
            return;
        }

        // This pilot's own run: closed out here and now — stopped and unlinked, never deleted — so the window is on
        // the site just copied. A group run is left standing, because ending it reaches every other member.
        if (RunId is { } runId && GroupCode is null && FleetId is null)
        {
            string? closed = SignatureName;   // read before _SetSignature moves it on to the copied site
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Send(new DiscardRunCommand(runId, DateTime.UtcNow));
            _ResetForNewRun();
            _SetSignature(id, group, name, sites);
            _SignatureDecision($"closed out the open {closed} run", name);
            if (RunLoot is not null)
                await RunLoot.RefreshAsync();
            Refresh(DateTime.UtcNow);
            return;
        }

        _pendingSignature = (id, group, name, sites);
        StopRun(DateTime.UtcNow);
        _SignatureDecision("the open run belongs to a group, so it waits", name);
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

    /// <summary>
    /// What this window did with a copied signature, and why — the line that says which of the two routes ran,
    /// after this bug survived four attempts because that was invisible.
    ///
    /// ponytail: temporary instrument, at Warning only because AppLogger drops everything below it and
    /// app-errors.jsonl is the only file a player can hand over. An ordinary copy has no business writing to an
    /// error log: take this out once Raymond confirms the site switch behaves, or give AppLogger a level that
    /// reaches that file without claiming something went wrong.
    /// </summary>
    private void _SignatureDecision(string what, string name) =>
        _services.GetService<ILoggerFactory>()?.CreateLogger<ActivityWindowViewModel>().LogWarning(
            "Copied signature {Signature}: {What} (run {RunId}, state {State}, group {Group}, fleet {Fleet}).",
            name, what, RunId, RunState, GroupCode, FleetId);

    private void _ApplyPendingSignature()
    {
        if (_pendingSignature is not { } pending)
            return;

        _pendingSignature = null;
        _SetSignature(pending.Id, pending.Group, pending.Name, pending.Sites);
    }

    private string _LootItemCount()
    {
        int items = RunLoot?.Captures.Where(capture => !capture.IsExcluded).Sum(capture => capture.Entries.Count) ?? 0;
        return items == 1 ? "1 item" : $"{items} items";
    }

    internal void ApplyFitDetection(ShipFitDetectionReading reading)
    {
        // The four detection states of ET-101 stay four: whether a character may look at all, has not looked yet,
        // looked and found nothing, or looked and found too much are different answers with different remedies.
        FitText = reading.State switch
        {
            ShipFitDetectionState.Unobserved => "no fit: ship type has not been read yet",
            ShipFitDetectionState.ScopeMissing => "no fit: ship-type scope is missing",
            ShipFitDetectionState.Observed when reading.MatchReason == ShipFitMatchReason.Detached =>
                "no fit: unlinked from this run",
            ShipFitDetectionState.Observed when reading.MatchReason == ShipFitMatchReason.NoFitFound =>
                "no fit: no known fit matches the observed ship",
            ShipFitDetectionState.Observed when reading.SelectedFit is { } fit =>
                $"fit: {fit.Name} ({_FitMatchReason(reading.MatchReason)})",
            ShipFitDetectionState.Observed => "no fit: no single fit matches the observed ship",
            _ => "no fit: ship fit is unavailable"
        };
        HasFit = reading is { State: ShipFitDetectionState.Observed, SelectedFit: not null };
        _RefreshSummaries();
    }

    internal void ApplyFitStats(FitStats? stats, bool fitCouldBeRead)
    {
        if (!fitCouldBeRead)
        {
            FitVelocityText = "fit could not be read";
            FitWarpSpeedText = "fit could not be read";
            return;
        }

        FitVelocityText = stats is null ? "no max velocity" : $"max velocity: {stats.MaxVelocity:N0} m/s";
        FitWarpSpeedText = stats is null ? "no warp speed" : $"warp speed: {stats.WarpSpeed:N2} AU/s";
    }

    private static string _FitMatchReason(ShipFitMatchReason? reason) => reason switch
    {
        ShipFitMatchReason.ShipName => "name matches the observed ship",
        ShipFitMatchReason.OnlyFitForShipType => "only known fit for this ship type",
        ShipFitMatchReason.Manual => "manual choice",
        _ => "automatic suggestion"
    };

    private string _ActivitySummary()
    {
        if (!IsAbyssal)
            return string.Join(" · ", new[] { SignatureName ?? "no signature", _ShortDemand(), SolarSystem }
                .Where(part => part is not null));

        return Weather is { } weather && TierIndex is { } tier
            ? $"{Tiers[tier]} T{tier} · {weather.Name} · no location"
            : "not set yet · no location";
    }

    /// <summary>Shut, the section still has to answer both halves of the question it exists for: which kinds were
    /// seen, and how many of them carry a count. A count of zero is not stored (ET-106), so "seen" and "counted"
    /// are different numbers and the header is the only place they are both visible.</summary>
    private string _EnemySummary()
    {
        int types = EnemyObservations.Count;
        if (types == 0)
            return RunState == ActivityRunState.NotStarted ? "no run watched yet" : "no enemies seen yet";

        int counted = EnemyObservations.Count(observation => observation.IsCounted);
        return $"{types} {(types == 1 ? "type" : "types")} · {(counted == 0 ? "none counted" : $"{counted} counted")}";
    }

    /// <summary>The shut header carries what the run demands, not only what it is called — the same description the
    /// site line and the toast use, so the three cannot drift apart. Silent when the entries sharing the name do
    /// not agree: a demand is worth nothing if it might be the neighbour's.</summary>
    private string? _ShortDemand() =>
        SdeSiteDescription.DescribeCommon(MatchedSites) is { Length: > 0 } common ? common : null;

    /// <summary>The hulls a site names, or null when it names none. A restricted site whose allow-list resolves to
    /// no groups says nothing here and stays "ship-restricted" on the site line — reading it as "anything goes" is
    /// the one mistake here that costs a ship.</summary>
    private static string? _ShipRule(SdeSite site) =>
        site is { IsShipRestricted: true, AllowedShipGroups: not [] }
            ? string.Join(", ", site.AllowedShipGroups.Select(group => group.Name).Order())
            : null;

    private void _AfterChoice()
    {
        IsPickerOpen = !HasWeatherAndTier;
        _SyncChoices();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>Mirror the selection onto the buttons. The choices carry it themselves so the picker can be a flat
    /// <c>ItemsControl</c> instead of five and seven hand-written buttons.</summary>
    private void _SyncChoices()
    {
        foreach (var choice in WeatherChoices)
            choice.IsSelected = choice.Index == WeatherIndex;

        foreach (var choice in TierChoices)
            choice.IsSelected = choice.Index == TierIndex;

        foreach (var choice in LootStrategyChoices)
            choice.IsSelected = choice.Label == LootStrategy;
    }

    private async Task _PersistAsync(string key, string value)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetSettingCommand(key, value));
    }

    /// <summary>The resist penalty is rolled per site rather than fixed per tier, so the window shows the band it
    /// can land in instead of a number it would be inventing — the same three strengths AbyssalBeacons offers as an
    /// explicit choice.</summary>
    private static string _PenaltyRange(int tier) => tier <= 3 ? "-30% or -50%" : "-50% or -70%";

    /// <summary>A stored index that no longer addresses anything is treated as unset — the alternative is a window
    /// that throws on open because a list got shorter.</summary>
    private static int? _Restore(string? stored, int count) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
        && index >= 0 && index < count
            ? index
            : null;

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
