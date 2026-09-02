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
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Control;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
// Aliased rather than imported wholesale: that namespace also holds an ActivityKind, which would collide with the
// window's own.
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

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

    /// <summary>How much of the pocket you opened. The two runs loot in different vocabularies, so they get
    /// different lists rather than one that half fits each.</summary>
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

    // The bounty lines seen while this run was running, with their own times — what SAVE writes as the run's
    // RunBountyEntry rows, and what the section adds up meanwhile.
    private readonly List<RunBountyEntryInput> _bounties = [];

    // The fleet's latest location sample per member, so the envelope is re-taken over the whole fleet on every
    // sample rather than over whichever one happened to arrive last.
    private readonly Dictionary<int, MetricSample> _fleetLocations = [];

    private DispatcherTimer? _timer;
    private bool _isManualRun;
    private int? _runCharacterId;
    private int? _namedCharacterId;
    private string? _runCharacterName;
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
        RunLoot = services.GetService<CqrsDispatcher>() is { } dispatcher ? new RunLootViewModel(dispatcher) : null;
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

    /// <summary>Which of the two runs this window is showing. Fixed for the life of the window: a run does not turn
    /// into the other kind halfway through.</summary>
    public ActivityKind Kind { get; }

    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations => _enemyObservations?.Observations ?? [];

    public RunLootViewModel? RunLoot { get; }

    public bool IsAbyssal => Kind == ActivityKind.Abyssal;

    /// <summary>The same kind as the run store names it. Two enums, deliberately mapped rather than cast: they are
    /// separate types and a silent reordering of either would otherwise file runs under the wrong activity.</summary>
    public StoredActivityKind StoredKind => IsAbyssal ? StoredActivityKind.Abyssal : StoredActivityKind.Site;

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
    private RunControlAuthority _authority = RunControlAuthority.From(null, null, null);

    /// <summary>The run row this window is writing to, once one has been started. Null for a window that is only
    /// showing the frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveButtonVisible))]
    private Guid? _runId;

    /// <summary>The fleet this run belongs to, or null when the window was never told of one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFleetShown))]
    private long? _fleetId;

    [ObservableProperty] private string? _groupCode;

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

    /// <summary>The list this run's kind loots by.</summary>
    public IReadOnlyList<string> LootStrategies => IsAbyssal ? AbyssalLootStrategies : SiteLootStrategies;

    /// <summary>What the copied signature's own text said it was (ET-100) — the raw scan-window field, not
    /// anything the SDE could enrich it to. Always null in the abyss; a filament carries no signature.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureTypeText))]
    [NotifyPropertyChangedFor(nameof(HasSignature))]
    private string? _signatureGroup;

    /// <summary>The signature's name once fully scanned — same field, same source as <see cref="SignatureGroup"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    [NotifyPropertyChangedFor(nameof(HasSignature))]
    private string? _signatureName;

    /// <summary>What the site catalogue carries under <see cref="SignatureName"/> (ET-80). Empty is the ordinary
    /// case rather than a fault — the match is on the English name only, so a miss cannot prove the site is absent —
    /// and so is more than one, since 218 catalogue names are shared by 613 dungeons. Nothing below ever picks one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureSiteText))]
    [NotifyPropertyChangedFor(nameof(ShipRestrictionText))]
    [NotifyPropertyChangedFor(nameof(HasShipRestriction))]
    private IReadOnlyList<SdeSite> _matchedSites = [];

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

    public string HeaderTitle => IsAbyssal ? "ABYSSAL RUN" : "SITE RUN";

    // Start, stop and discard steer the run for everybody in it, so all three hang off the same authority (AC-4).
    // Start and stop are the same slot seen from two sides and never both apply: a run that is going can only be
    // stopped, and offering to re-start it over itself is what put START next to a ticking clock.
    public bool IsStartButtonVisible => Authority.CanControl && RunState != ActivityRunState.Running;

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

    // ── Who was on the run ──────────────────────────────────────────────────────────────────────────

    public ObservableCollection<RunParticipantViewModel> Participants { get; } = [];

    /// <summary>Shown over every payout figure. The window reports an expectation, and never implies EVE's own
    /// payout rule follows our exclusions.</summary>
    public string PayoutExpectationLabel => RunPayoutSplit.ExpectationLabel;

    /// <summary>Who this window has actually heard from, one row per member that sent a location sample. Never a
    /// roster: nothing here can see a member who is not sharing their location, which is what
    /// <see cref="FleetBasisText"/> is on screen to say.</summary>
    public ObservableCollection<ActivityFleetMemberViewModel> FleetMembers { get; } = [];

    public string FleetStatusText => FleetMemberCount > 1
        ? IsAbyssal
            ? $"based on {AnchoredFleetMemberCount} of {FleetMemberCount} members sharing a location"
            : $"based on {FleetMemberCount} members sharing a location"
        : "no other member has reported in yet";

    /// <summary>
    /// What the count is counted from, said outright. <see cref="FleetMemberCount"/> counts location samples, so a
    /// member who does not share their location is missing from both the number and the list — and a list of two
    /// names in a fleet of three is a lie unless it says what it is a list of.
    /// </summary>
    public string FleetBasisText => FleetMembers.Count == 0
        ? "No member has shared a location yet, so there is nobody to list."
        : "Counted from shared locations. A member not sharing theirs is in the fleet but not in this list.";

    /// <summary>
    /// Whether there is a fleet to show at all. Nothing here may claim "solo": the window is never told the pilot
    /// is alone, it is only ever told about a fleet — by the commander's own start (which sets
    /// <see cref="FleetId"/>) or by a member's sample arriving on the bus. Without either, the section is not
    /// collapsed but gone, because an empty FLEET section reads as a measurement and it is not one.
    /// </summary>
    public bool IsFleetShown =>
        FleetId is not null || _fleetLocations.Count > 0 || Participants.Count > 0 || FleetMembers.Count > 0;

    public string RunOriginText => RunState == ActivityRunState.NotStarted
        ? "not started"
        : _isManualRun ? "manual" : "estimated from fleet";

    /// <summary>
    /// What the clock does not say on its face. <c>AbyssalSpace.Describe</c> writes a "+" for this; here it is a
    /// sentence under the figure instead, which is where it ended up after the first round of review.
    /// </summary>
    public string ClockHint => IsAbyssal
        ? (FleetMemberCount > 1 ? $"{FleetStatusText}: the envelope is the earliest anchored run. " : string.Empty)
          + "The clock is a floor — the moment of entry cannot be observed, so this is at most what is left."
        : RunState == ActivityRunState.Stopped
            // STOP is a pause, not an end (Raymond, 2026-09-02): stepping out mid-site and coming back has to cost
            // you nothing, so START picks the same run back up. What ends a run is SAVE or DISCARD.
            ? "Stopped runs keep their figures; start picks this run back up."
            : "Manual start and stop are the only source for a site run.";

    // ── Section bodies ──────────────────────────────────────────────────────────────────────────────

    public bool IsInsideAbyssal => InsideAbyssal ?? IsAbyssal;

    public string LocationText => IsInsideAbyssal
        ? "none — an abyssal pocket has no location"
        : LocationDisplay ?? SolarSystem ?? "not known yet";

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

    public string TierText => TierIndex is { } tier
        ? $"{Tiers[tier]} (Tier {tier})"
        : "not set";

    public string WeatherEnvironmentText => Weather?.EnvironmentName ?? "not set";

    public string WeatherEffectText => Weather is { } weather && TierIndex is { } tier
        ? $"{weather.Bonus} · {_PenaltyRange(tier)} {weather.PenaltyTarget}"
        : "not set";

    /// <summary>The caption over every ISK figure in the loot section. It names its own source on purpose: the
    /// figures are whatever price column happened to be in the EVE loot window at the moment of the copy, and the
    /// window has no way to price anything itself.</summary>
    public string IskLabel => "Prices are the clipboard column as it stood at the copy.";

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
        await _AdoptRunningRunAsync();
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(DateTime.UtcNow);
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

        Character? chosen = known is [{ } only] ? only : null;
        if (chosen is null && mayAsk && known.Count > 1
            && _services.GetService<IDialogService>() is { } dialogs)
        {
            int? picked = await dialogs.PickCharacterAsync("Whose run is this?",
                [.. known.Select(character => new CharacterPickOption(
                    character.EsiCharacterId!.Value, character.Name, "local character", Enabled: true))]);
            chosen = known.FirstOrDefault(character => character.EsiCharacterId == picked);
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
        if (!running.IsSuccess || running.Value is not { } run || run.ActivityKind != StoredKind)
            return false;

        RunId = run.Id;
        AnchorUtc = run.StartedAtUtc;
        StoppedAtUtc = null;
        GroupCode ??= run.GroupCode;
        // The adopted run brings its own site. Without this the window wore whichever signature had just been
        // copied over a run belonging to somewhere else entirely — Raymond opened one on Blood Burrow and got a
        // Sansha Refuge run's clock, loot and fit under that heading (2026-09-02). The clock and the loot are the
        // run's, so the name over them has to be the run's too; the newly copied signature is not this run's and
        // does not belong on it.
        if (run.SiteName is { Length: > 0 } siteName && siteName != SignatureName)
        {
            SignatureName = siteName;
            MatchedSites = [];
        }
        _isManualRun = true;
        RunState = ActivityRunState.Running;
        await _AdoptCharacterAsync(checked((int)run.CharacterId));
        _StartEnemyObservations();
        return true;
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

        IEnumerable<FleetParticipant> mine = _services.GetService<IFleetParticipation>()?.Current ?? [];
        if (FleetId is { } fleetId)
            mine = mine.Where(participant => participant.FleetId == fleetId);
        return mine.Select(participant => participant.CharacterId).Distinct().ToList() is [{ } only] ? only : null;
    }

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

        using var scope = _services.CreateScope();
        Result<Guid> started = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(
            new StartRunCommand(characterId, StoredKind, startedAtUtc,
                // No type id: a signature names a dungeon, and the catalogue's DungeonId is not the type id this
                // column holds. The name travels instead.
                SiteTypeId: 0,
                SiteName: SignatureName,
                SolarSystemId: null,
                GroupCode: GroupCode,
                Signature: SignatureGroup,
                FleetId: FleetId));
        if (!started.IsSuccess)
        {
            _services.GetService<IToastService>()?.Show("Run not started",
                started.Messages.FirstOrDefault()?.Text ?? "Could not start this run.", ToastKind.Error);
            return;
        }

        RunId = started.Value;
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
        Refresh(nowUtc);
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
    /// the old one, and that is an ordinary state change (ET-105). <paramref name="fleetBossCharacterId"/> null
    /// means ESI cannot say — the controls go away and say why, rather than appearing for everybody.
    /// </summary>
    public void ApplyFleetCommand(long? fleetId, int? fleetBossCharacterId, int? actingCharacterId)
    {
        FleetId = fleetId;
        Authority = RunControlAuthority.From(fleetId, fleetBossCharacterId, actingCharacterId);
    }

    /// <summary>
    /// Where the two halves of that question come from. The fleet is the one this client is participating in —
    /// what the run is filed under and what a discard fans out over. The boss is whoever ESI reports commands it at
    /// this moment, via <see cref="FleetBossTracker"/>; null when ESI cannot say, which lands on
    /// <see cref="RunControlAuthorityLevel.Unknown"/> and says so on screen rather than handing a destructive button
    /// to everybody. Run on the tick, like the location is, so a handover moves the controls on its own.
    /// </summary>
    public async Task RefreshFleetCommandAsync(DateTime nowUtc)
    {
        IActiveFleetState? fleet = _services.GetService<IActiveFleetState>();
        // Before START the run has no character yet, so fall back to the one this client is in the fleet as —
        // otherwise a multi-character client could not reach START to resolve it.
        //
        // Deliberately NOT _ActingCharacterId(), which the FIT section uses: both halves of this decision — which
        // fleet, and as whom — have to come from the same place, or the boss of one fleet is compared against a
        // character selected in another. IActiveFleetState is empty until the fleets window's row selection fills
        // it, and a null fleet id makes RunControlAuthority grant outright, so on Raymond's client this gate is
        // currently inert. Making it live means deciding who loses the DISCARD button, which is ET-105's call and
        // not this fix's.
        int? actingCharacterId = _runCharacterId ?? fleet?.CharacterId;
        if (actingCharacterId is not { } characterId || _services.GetService<FleetBossTracker>() is not { } bosses)
        {
            ApplyFleetCommand(fleet?.ActiveFleetId, null, actingCharacterId);
            return;
        }

        await bosses.RefreshAsync(characterId, nowUtc);
        ApplyFleetCommand(fleet?.ActiveFleetId, bosses.BossOf(characterId), characterId);
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
            SaveFailureText = "This run was never registered, so there is nothing to save it to.";
            _services.GetService<IToastService>()?.Show("Run not saved", SaveFailureText, ToastKind.Error);
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
            SaveFailureText = result.Messages.FirstOrDefault()?.Text ?? "Could not save this run.";
            _services.GetService<IToastService>()?.Show("Run not saved", SaveFailureText, ToastKind.Error);
            return;
        }

        SaveFailureText = null;
        RunState = ActivityRunState.Saved;
        _EndEnemyObservations();
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(nowUtc);
        // Only here, and only for this window: the run is committed and there is nothing left to do to it. A failed
        // save falls out above with the reason still on screen, and a group's other members keep their own windows —
        // saving is each member's own, and only the FC's DISCARD reaches anybody else (ET-105).
        SaveSucceeded?.Invoke();
    }

    /// <summary>Raised once a save has actually landed. The window closes on it; nothing else listens, and nothing
    /// crosses to another member's window.</summary>
    public event Action? SaveSucceeded;

    /// <summary>Why the last save did not land, left on screen beside the still-open window. A toast is gone in
    /// seconds and this is the state that says the work is not stored yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveFailure))]
    private string? _saveFailureText;

    public bool HasSaveFailure => SaveFailureText is not null;

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

        if (FleetId is { } fleetId && GroupCode is { } groupCode)
            await _services.GetRequiredService<IEventBus>().PublishAsync(
                new FleetRunDiscardedEvent(new RunGroupDiscard(fleetId, StoredKind, groupCode, nowUtc)),
                EventTarget.Both);

        // Thrown away means gone from this window too: the next START is a new run, not a second attempt at this one.
        _ResetForNewRun();
        GroupCode = null;   // the group ended with the run, which is what a discard reaches the other members to say.
        if (RunLoot is not null)
            await RunLoot.RefreshAsync();
        Refresh(nowUtc);
    }

    /// <summary>
    /// Everything the last run left standing on this window, in one place. A new run starts clean (Raymond,
    /// 2026-09-02): what survives a run survives because it was decided to, not because nobody cleared it, and one
    /// method rather than three copies is the whole point — three drift apart the first time a field is added, which
    /// is how this gap opened.
    ///
    /// START is deliberately not one of its callers: STOP is a pause, so pressing START again picks the same run
    /// back up. Ending a run is SAVE or DISCARD, and only DISCARD comes back here — SAVE closes the window.
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
        SaveFailureText = null;
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
        if (sample.Kind != MetricKind.Location || (FleetId is { } fleetId && sample.FleetId != fleetId))
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
        foreach (MetricSample sample in members)
        {
            ActivityFleetMemberViewModel? row = FleetMembers.FirstOrDefault(m => m.CharacterId == sample.CharacterId);
            if (row is null)
            {
                row = new ActivityFleetMemberViewModel(sample.CharacterId);
                FleetMembers.Add(row);
                _ = _ResolveFleetMemberNameAsync(row);
            }

            row.LocationText = sample.AbyssalAnchorMs > 0
                ? "in abyssal space"
                : sample.Text ?? "not sharing a system";
        }

        foreach (ActivityFleetMemberViewModel gone in FleetMembers
                     .Where(row => members.All(sample => sample.CharacterId != row.CharacterId)).ToList())
            FleetMembers.Remove(gone);

        OnPropertyChanged(nameof(IsFleetShown));
        OnPropertyChanged(nameof(FleetBasisText));
    }

    /// <summary>The registry first — a local character is known without asking anyone — then public ESI, the same
    /// route the fleet overlay resolves its rows by. Best-effort: an unresolved id keeps its "Char 90000001" label,
    /// which is still a member you can count.</summary>
    private async Task _ResolveFleetMemberNameAsync(ActivityFleetMemberViewModel member)
    {
        if (_services.GetService<ICharacterRegistry>() is { } registry
            && (await registry.GetAllAsync()).FirstOrDefault(c => c.EsiCharacterId == member.CharacterId) is { } local)
        {
            member.Name = local.Name;
            return;
        }

        if (_services.GetService<IExternalCharacterLookup>() is not { } lookup)
            return;

        ExternalCharacterInfo info = await lookup.LookupAsync(member.CharacterId);
        if (info.Exists)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => member.Name = info.Name);
    }

    private async Task _BeginEstimatedRunAsync(DateTime anchorUtc)
    {
        if (await _AdoptRunningRunAsync() || !await _ResolveCharacterAsync(mayAsk: false))
            return;

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
        Loot.HeaderSummary = RunLoot?.Captures.Count > 0
            ? RunLoot.NetIskDisplay
            : RunLoot?.RunStatusMessage ?? "no loot captured";
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
