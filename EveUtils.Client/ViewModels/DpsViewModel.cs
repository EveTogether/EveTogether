using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Controls;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Live combat meter for ONE character (yourself or a fleet member): the figures, the meters beside them and the
/// graph. Every quantity is one <see cref="RateLine"/> — DPS out and in, and reps, neut and cap each split into
/// received and given (ET-277: summing the directions made being neuted read like neuting). Every line renders
/// through the same shared path (<see cref="DpsRenderDriver"/> → <see cref="StepFrame"/>), so the own meters and the
/// fleet-member meters stay identical.
///
/// The numbers and meters show each rate as measured; only the graph line is smoothed, for display. A calm line and an
/// honest number: the smoothing that makes a curve pleasant to follow would otherwise also make the number lag.
/// </summary>
public partial class DpsViewModel : ViewModelBase, IFleetMemberMenuHost
{
    private const int GraphCapacityValue = 9000;  // ~5min at 30fps — max retained history; the graph draws a fixed
                                                   // pixels-per-second slice of it anchored right, so a wider graph
                                                   // shows a longer timeline (covers fullscreen/ultrawide). Cheap: a
                                                   // ring buffer, and render only walks the visible samples
    private const double Smoothing = 0.15;        // EMA coefficient for the 30fps render path (the graph line only)

    // The flags switch on at one figure and off at a lower one, so a value hovering at the edge does not blink them.
    // UNDER FIRE: taking this much more than the reps coming in. NEUTED: this much energy neutralized on you.
    internal const double UnderFireOn = 150, UnderFireOff = 100;
    internal const double NeutedOn = 5, NeutedOff = 3;

    // How often the application sampler is asked, in frames: a verdict over 20 s of shots has nothing new 30× a second.
    private const int ApplicationEveryFrames = 15;

    // Fixed inks (CombatInk), never the faction accent. Drawn in this order, so OUT ends up on top of its lane.
    private readonly RateLine _repOutLine = new(MetricKind.RepOut, CombatInk.Rep, GraphLane.HitPoints, dashed: true);
    private readonly RateLine _repInLine = new(MetricKind.RepIn, CombatInk.Rep, GraphLane.HitPoints);
    private readonly RateLine _inLine = new(MetricKind.DpsIn, CombatInk.In, GraphLane.HitPoints);
    private readonly RateLine _outLine = new(MetricKind.Dps, CombatInk.Out, GraphLane.HitPoints);
    private readonly RateLine _capOutLine = new(MetricKind.CapOut, CombatInk.Cap, GraphLane.Capacitor, dashed: true);
    private readonly RateLine _capInLine = new(MetricKind.CapIn, CombatInk.Cap, GraphLane.Capacitor);
    private readonly RateLine _neutOutLine = new(MetricKind.NeutOut, CombatInk.Neut, GraphLane.Capacitor, dashed: true);
    private readonly RateLine _neutInLine = new(MetricKind.NeutIn, CombatInk.Neut, GraphLane.Capacitor);
    private readonly IReadOnlyList<RateLine> _lines;

    private readonly List<GraphMarker> _markers = [];

    // A local meter pulls its live (decaying) rates from the gamelog each frame; a remote/fleet meter has no sampler
    // and is fed each line's latest value via SetRate. Either way the render frame smooths toward the target the same way.
    private Func<CombatRates?>? _sampler;
    private Func<ApplicationSummary?>? _applicationSampler;
    private int _frame;

    // What this member has been seen doing since the meter opened: a support row appears the first time it is needed
    // and stays, rather than blinking in and out with every cycle.
    private bool _hasDealt, _hasRepaired, _hasTransferredCap, _hasNeutralized;

    [ObservableProperty] private long _dealt;
    [ObservableProperty] private long _received;

    /// <summary>Energy neutralized ON this member, GJ/s — the half the fleet overlay names ("who is being neuted").</summary>
    [ObservableProperty] private long _neutIn;

    /// <summary>Energy this member neutralizes on others, GJ/s.</summary>
    [ObservableProperty] private long _neutOut;

    [ObservableProperty] private long _capIn;
    [ObservableProperty] private long _capOut;
    [ObservableProperty] private long _repIn;
    [ObservableProperty] private long _repOut;

    /// <summary>Each meter's fill against <see cref="Scale"/>: 0–1.</summary>
    [ObservableProperty] private double _outFraction;
    [ObservableProperty] private double _inFraction;
    [ObservableProperty] private double _repOutFraction;
    [ObservableProperty] private double _capOutFraction;
    [ObservableProperty] private double _neutOutFraction;
    [ObservableProperty] private double _neutInFraction;

    /// <summary>The tops of the graph's two lanes, from <see cref="Scale"/>.</summary>
    [ObservableProperty] private double _hitPointsScale = CombatScale.HitPointsFloor;
    [ObservableProperty] private double _capacitorScale = CombatScale.CapacitorFloor;

    /// <summary>Taking a lot more than the reps coming in (<see cref="UnderFireOn"/> hp/s net).</summary>
    [ObservableProperty] private bool _isUnderFire;

    /// <summary>Being neuted (<see cref="NeutedOn"/> GJ/s or more on this member).</summary>
    [ObservableProperty] private bool _isNeuted;

    /// <summary>Which meter rows this member needs: DPS out unless they only support, and a row per kind of support
    /// they give — REP OUT for a logi, CAP OUT for a cap chain, NEUT OUT for a neut boat.</summary>
    [ObservableProperty] private bool _showsOut = true;
    [ObservableProperty] private bool _showsRepOut;
    [ObservableProperty] private bool _showsCapOut;
    [ObservableProperty] private bool _showsNeutOut;

    /// <summary>The line beside IN: the reps coming in and what is left of the damage after them.</summary>
    [ObservableProperty] private string? _inDetail;

    /// <summary>The line beside NEUT IN: the cap coming in against it.</summary>
    [ObservableProperty] private string? _neutInDetail;

    /// <summary>How well this member's main weapon lands on its current target (ET-277).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplication))]
    [NotifyPropertyChangedFor(nameof(ApplicationText))]
    [NotifyPropertyChangedFor(nameof(ApplicationShort))]
    [NotifyPropertyChangedFor(nameof(ApplicationBreakdown))]
    [NotifyPropertyChangedFor(nameof(IsApplicationSweetSpot))]
    [NotifyPropertyChangedFor(nameof(IsApplicationOk))]
    [NotifyPropertyChangedFor(nameof(IsApplicationAdjust))]
    [NotifyPropertyChangedFor(nameof(IsApplicationUnjudged))]
    private ApplicationSummary _application = ApplicationSummary.Idle;

    [ObservableProperty] private string _character = "—";

    /// <summary>The EVE character this meter belongs to, or 0 where there is none to name (the design-time meter,
    /// the own-meter placeholders). Fleet metrics persists its member order as a list of these.</summary>
    [ObservableProperty] private int _characterId;

    /// <summary>This member is being dragged to a new place in the fleet-metrics list, so its row is showing as the
    /// spot it came from while a ghost follows the cursor. Only that screen's templates read it.</summary>
    [ObservableProperty] private bool _isDragging;

    /// <summary>When this member's last metric sample of any kind arrived (fleet metrics); null = none yet. Read
    /// only when the member menu is built, so it stays a plain property — an FC asking "is this pilot still
    /// publishing" wants the answer at the moment they ask, not a value that ticks 40 rows a second.</summary>
    public DateTimeOffset? LastSampleAt { get; set; }

    /// <summary>
    /// When the server last saw this member's client publish into the fleet, off the roster read; null = never. It
    /// answers the one thing <see cref="LastSampleAt"/> cannot: a screen that has just opened has heard nobody yet,
    /// so without this every member would look equally silent for the first minute and a half.
    /// </summary>
    public DateTimeOffset? ServerLastSeenAt { get; set; }

    /// <summary>The most recent contact from this pilot's client from either account of it. Null = they have never
    /// been heard from at all, which is not the same as having gone quiet.</summary>
    public DateTimeOffset? LastHeardAt => (LastSampleAt, ServerLastSeenAt) switch
    {
        ({ } sample, { } server) => sample > server ? sample : server,
        ({ } sample, null) => sample,
        (null, { } server) => server,
        _ => null,
    };

    /// <summary>The shared right-click menu for this member (ET-44), rebuilt as the menu opens so its live lines are
    /// current. Empty off the fleet-metrics screen — the own meters and the DPS pop-out are not roster rows.</summary>
    [ObservableProperty] private IReadOnlyList<FleetMemberMenuItemViewModel> _memberMenu = [];
    [ObservableProperty] private bool _isSelf;
    [ObservableProperty] private int _graphRevision;

    /// <summary>Whether an EVE client for this character is running on this machine (home dashboard presence dot);
    /// set best-effort by the dashboard from <c>EveClientPresenceService</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Presence))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(KnownLocation))]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private bool _inEve;

    /// <summary>
    /// One of <b>this</b> client's own characters, so <see cref="InEve"/> is evidence about them rather than the
    /// absence of it. False for a fleet mate on another machine: their EVE client is invisible to us and nothing
    /// may be inferred from not seeing it — that stays ET-70's question.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Presence))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(KnownLocation))]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private bool _isLocalCharacter;

    /// <summary>What this member's own client last said about their game (ET-70). Only ever set from a
    /// <see cref="MetricKind.Presence"/> sample, so a member on a client too old to send one stays
    /// <see cref="PresenceState.Unknown"/> and goes on being read exactly as before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Presence))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(KnownLocation))]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private PresenceState _reportedPresence = PresenceState.Unknown;

    /// <summary>
    /// This member was publishing and has stopped for longer than <see cref="FleetMemberPresence.SilentAfter"/> —
    /// their EVE Together is gone, which is the one state no message can ever report. Pushed in by the screen's own
    /// slow sweep rather than derived here, because it moves with the wall clock and nothing else: no property on
    /// this row changes at the moment a pilot becomes silent, so nothing here could raise it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Presence))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(KnownLocation))]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private bool _isSilent;

    /// <summary>
    /// Everything known about whether this pilot is here, folded into one verdict by
    /// <see cref="FleetMemberPresence.Read"/> — the single definition, so a row, the header badge and the pop-out
    /// cannot tell three stories. <see cref="FleetMemberPresenceState.Unknown"/> is a state of its own and not a
    /// quiet "online": a pilot who shares nothing has never been evidence of anything.
    /// </summary>
    public FleetMemberPresenceState Presence => FleetMemberPresence.Read(
        IsLocalCharacter ? InEve : null, ReportedPresence, IsSilent);

    /// <summary>
    /// Known not to be in game. ESI answers <c>/location/</c> for a logged-out character with the spot they logged
    /// off at, which looks on screen exactly like a current position and is not one — the operator read a fleet as
    /// all standing in Amarr while three of five accounts were closed (ET-71).
    ///
    /// This is the single verdict behind all three consequences, which must never disagree: no system is shown, the
    /// member is left out of the WITH FC denominator, and they are never coloured green.
    /// </summary>
    public bool IsOffline => Presence is FleetMemberPresenceState.Offline;

    /// <summary>
    /// Re-read the silence half of <see cref="Presence"/> against the clock. Driven by the owning screen's slow
    /// sweep and given the time rather than reading it, so a test decides when "now" is.
    /// </summary>
    public void RefreshPresence(DateTimeOffset now) =>
        IsSilent = FleetMemberPresence.IsSilent(LastHeardAt, now);

    /// <summary>True for a real live tracker (has a running graph); false for a home-dashboard placeholder — a
    /// character with no live combat yet. An online (in-EVE) placeholder is still shown as a normal row with its
    /// location; only a truly offline one is greyed (see <see cref="ShowOffline"/>).</summary>
    [ObservableProperty] private bool _isLive = true;

    /// <summary>The character is offline AND has no live combat → greyed "offline" row. An in-EVE character with no
    /// combat yet is NOT offline: it shows as a normal row with its location and an empty graph until combat starts.</summary>
    public bool ShowOffline => !InEve && !IsLive;

    partial void OnInEveChanged(bool value) => OnPropertyChanged(nameof(ShowOffline));
    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(ShowOffline));

    /// <summary>The character's ESI portrait for the dashboard row hex; null → the initial-glyph fallback. Loaded
    /// best-effort by the dashboard (opt-in images).</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;
    partial void OnPortraitChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>First letter of the character name for the hex glyph fallback.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Character) || Character == "—" ? "?" : Character[..1].ToUpperInvariant();

    /// <summary>The member's current solar system (fleet metrics, when location is shared); null = unknown/not shared.
    /// The reported value, whatever its age — read <see cref="KnownLocation"/> to decide anything with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KnownLocation))]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private string? _location;

    /// <summary>When this character's abyssal countdown started, or null when they are not in one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private DateTime? _abyssalAnchorUtc;

    /// <summary>
    /// Why the ESI location watch has nothing to report, when <see cref="Location"/> is null — set from
    /// <see cref="EveUtils.Shared.Modules.Gamelog.Aggregation.CharacterMetricsSnapshot.LocationUnavailableReason"/>
    /// for a local character only (ET-96). Null both when a system is known and when nothing has been heard from
    /// the watch yet, so a pilot who simply has not had a reading land still reads as blank, not as a refusal.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SystemDisplay))]
    private EsiErrorKind? _locationUnavailableReason;

    /// <summary>
    /// The location anything may rely on: the reported system, or null once we know the pilot is not in game. The
    /// badge counts this, <c>FleetCommanderPresence.IsWith</c> colours from this, and the readout below shows it —
    /// so the member who drops out of the count is exactly the member who shows no system, by construction.
    /// </summary>
    public string? KnownLocation => IsOffline ? null : Location;

    /// <summary>The one text every location readout binds to, so the screens showing a location cannot drift
    /// apart on what follows the system name. A pilot we know to be offline reads as that instead of as a system
    /// they left hours ago — a blank would be indistinguishable from "shares no location". Online with no system
    /// falls back to why, when the watch has said why (ET-96) — otherwise it stays blank: nothing heard yet is not
    /// itself a refusal.</summary>
    public string? LocationDisplay => IsOffline
        ? "offline"
        : EveUtils.Shared.Modules.Gamelog.Aggregation.AbyssalSpace.Describe(Location, AbyssalAnchorUtc, DateTime.UtcNow)
          ?? EsiLocationReasonText.Describe(LocationUnavailableReason);

    /// <summary>
    /// The system alone, dropped entirely when the pilot is offline. For the one readout that already names the
    /// online state right beside it — the home card's "In EVE" / "Offline" label — where
    /// <see cref="LocationDisplay"/> would read "Offline · offline".
    /// </summary>
    public string? SystemDisplay => IsOffline ? null : LocationDisplay;

    /// <summary>Re-read <see cref="LocationDisplay"/>. The countdown moves with the wall clock, not with the
    /// properties feeding it — a run's system and anchor both stay put, so nothing else would raise it.</summary>
    public void RefreshLocationDisplay()
    {
        OnPropertyChanged(nameof(LocationDisplay));
        OnPropertyChanged(nameof(SystemDisplay));
    }

    /// <summary>The member stands in the fleet commander's solar system (fleet metrics), which colours their location
    /// readout. Set from <see cref="EveUtils.Client.Fleet.FleetCommanderPresence"/> — the same source the header badge
    /// counts with — and false wherever there is no commander to stand with, e.g. the home dashboard's own meters.</summary>
    [ObservableProperty] private bool _isWithCommander;

    /// <summary>The member's cumulative session bounty (fleet metrics, when bounty is shared). 0 = none/not shared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    [NotifyPropertyChangedFor(nameof(HasBounty))]
    private long _bounty;

    /// <summary>The bounty compacted for display (e.g. 12.4M ISK).</summary>
    public string BountyText => CompactIsk(Bounty);

    /// <summary>Whether to show the bounty line (a member shares bounty and has earned some).</summary>
    public bool HasBounty => Bounty > 0;

    internal static string CompactIsk(long isk) => IskFormat.Compact(isk) + " ISK";

    /// <summary>Design-time constructor (XAML previewer).</summary>
    public DpsViewModel()
    {
        _lines = [_repOutLine, _repInLine, _inLine, _outLine, _capOutLine, _capInLine, _neutOutLine, _neutInLine];
        Series = _lines.Select(line => line.Series).ToList();
    }

    public DpsViewModel(string character, bool isSelf) : this()
    {
        Character = character;
        IsSelf = isSelf;
    }

    public IReadOnlyList<DpsSeries> Series { get; }

    /// <summary>Event markers on the time axis; ages tracked in lockstep with the scrolling series.</summary>
    public IReadOnlyList<GraphMarker> Markers => _markers;

    /// <summary>The scale this meter's bars and graph lanes are drawn against. A meter on its own keeps its own; the
    /// fleet screen hands every member the same one, so the cards compare.</summary>
    public CombatScale Scale { get; set; } = new();

    /// <summary>What the verdict chip means, for its tooltip on every screen that shows one.</summary>
    public const string ApplicationTooltip =
        "How well the main weapon lands on the target it is shooting now, from the hit-quality word of each of its " +
        "shots in the last 20 seconds (a miss counts as nothing, a wreck as a smash). Below 55 %: ADJUST. " +
        "From 80 %: SWEET SPOT. Missiles always log \"Hits\", however they land, so they get no percentage.";

    public bool HasApplication => Application.Verdict is not ApplicationVerdict.Idle;

    /// <summary>The verdict in a few characters, for the one-line compact row.</summary>
    public string ApplicationShort => Application.Verdict switch
    {
        ApplicationVerdict.SweetSpot => $"◆ {Application.Percent:0}%",
        ApplicationVerdict.Ok => $"● {Application.Percent:0}%",
        ApplicationVerdict.Adjust => $"▲ {Application.Percent:0}%",
        ApplicationVerdict.NotEnoughShots => "○ —",
        ApplicationVerdict.NotMeasurable => "○ n/a",
        _ => string.Empty,
    };

    /// <summary>The verdict chip: glyph, word and — when there is one — the percentage. Never colour alone.</summary>
    public string ApplicationText => Application.Verdict switch
    {
        ApplicationVerdict.SweetSpot => $"◆ SWEET SPOT · {Application.Percent:0}%",
        ApplicationVerdict.Ok => $"● OK · {Application.Percent:0}%",
        ApplicationVerdict.Adjust => $"▲ ADJUST · {Application.Percent:0}%",
        ApplicationVerdict.NotEnoughShots => "○ NOT ENOUGH SHOTS",
        ApplicationVerdict.NotMeasurable => "○ APPLICATION n/a",
        _ => string.Empty,
    };

    /// <summary>Every weapon's own figure, main weapon first: "Mega Pulse Laser II 74% · Acolyte II 100%".</summary>
    public string? ApplicationBreakdown => Application.Breakdown;

    public bool IsApplicationSweetSpot => Application.Verdict is ApplicationVerdict.SweetSpot;
    public bool IsApplicationOk => Application.Verdict is ApplicationVerdict.Ok;
    public bool IsApplicationAdjust => Application.Verdict is ApplicationVerdict.Adjust;
    public bool IsApplicationUnjudged => Application.Verdict is ApplicationVerdict.NotEnoughShots or ApplicationVerdict.NotMeasurable;

    /// <summary>Apply a sample — call on the UI thread. Ages existing markers one column. Raw (no EMA); used by
    /// the event-driven remote-member path, which only carries DPS out and in.</summary>
    public void Apply(DpsSampleDto sample)
    {
        _outLine.Target = _outLine.Ema = sample.DealtPerSecond;
        _inLine.Target = _inLine.Ema = sample.ReceivedPerSecond;
        Append(_outLine.Series, _outLine.Ema);
        Append(_inLine.Series, _inLine.Ema);
        RefreshFigures();
        AgeMarkers();
        GraphRevision++;
    }

    /// <summary>A full set of rates for one frame, from a caller that samples on its own clock (the metrics window).</summary>
    public void ApplyRates(CombatRates rates)
    {
        SetTargets(rates);
        StepFrame();
    }

    /// <summary>Feed a local meter from the gamelog each frame: the render driver calls the sampler, which returns the
    /// current decaying rates (or null when this character has no live local tracker yet — then the frame is skipped
    /// and the event-driven remote path owns the series).</summary>
    public void UseSampler(Func<CombatRates?> sampler) => _sampler = sampler;

    /// <summary>Feed a local meter's application chip from the gamelog, a couple of times a second.</summary>
    public void UseApplicationSampler(Func<ApplicationSummary?> sampler) => _applicationSampler = sampler;

    /// <summary>Set one line's latest value for a remote (fleet) meter; the figures take it as it is and the shared
    /// driver smooths the graph line toward it each frame, so a fleet graph scrolls and decays exactly like the own
    /// pop-out instead of stepping at 1 Hz. Unknown kinds are ignored (a newer client's metric kind degrades
    /// gracefully), and so are the combined <see cref="MetricKind.Neut"/> and <see cref="MetricKind.Cap"/> that
    /// clients keep sending for older ones: this meter shows the two directions apart.</summary>
    public void SetRate(MetricKind kind, double value)
    {
        foreach (var line in _lines)
            if (line.Kind == kind)
                line.Target = value;
    }

    /// <summary>A fleet member's application as their client reported it.</summary>
    public void SetApplication(ApplicationSummary application) => Application = application;

    /// <summary>One render frame, driven by the shared <see cref="DpsRenderDriver"/> for every graph. A local meter
    /// refreshes its targets from the sampler first; a remote meter uses the last <see cref="SetRate"/> values.</summary>
    public void RenderFrame()
    {
        if (_sampler is not null)
        {
            var sample = _sampler();
            if (sample is null)
                return; // remote character on the main list — its event-driven Apply path owns the series
            SetTargets(sample.Value);
        }

        if (_applicationSampler is not null && _frame++ % ApplicationEveryFrames == 0 && _applicationSampler() is { } application)
            Application = application;

        StepFrame();
    }

    private void SetTargets(CombatRates rates)
    {
        _outLine.Target = rates.Dealt;
        _inLine.Target = rates.Received;
        _repInLine.Target = rates.RepIn;
        _repOutLine.Target = rates.RepOut;
        _neutInLine.Target = rates.NeutIn;
        _neutOutLine.Target = rates.NeutOut;
        _capInLine.Target = rates.CapIn;
        _capOutLine.Target = rates.CapOut;
    }

    // The single smoothing + scroll core shared by every combat graph (own + fleet): EMA every line toward its target,
    // append a frame, refresh the figures, age the markers. A tweak here lands on all graphs and all lines at once.
    private void StepFrame()
    {
        foreach (var line in _lines)
        {
            line.Ema += Smoothing * (line.Target - line.Ema);
            Append(line.Series, line.Ema);
        }

        RefreshFigures();
        AgeMarkers();
        GraphRevision++;
    }

    // The figures, the meters and the flags all read the measured rate, not the smoothed line.
    private void RefreshFigures()
    {
        Dealt = (long)_outLine.Target;
        Received = (long)_inLine.Target;
        RepIn = (long)_repInLine.Target;
        RepOut = (long)_repOutLine.Target;
        NeutIn = (long)_neutInLine.Target;
        NeutOut = (long)_neutOutLine.Target;
        CapIn = (long)_capInLine.Target;
        CapOut = (long)_capOutLine.Target;

        Scale.Observe(
            Math.Max(Math.Max(_outLine.Target, _inLine.Target), Math.Max(_repInLine.Target, _repOutLine.Target)),
            Math.Max(Math.Max(_neutInLine.Target, _neutOutLine.Target), Math.Max(_capInLine.Target, _capOutLine.Target)),
            DateTime.UtcNow);
        HitPointsScale = Scale.HitPoints;
        CapacitorScale = Scale.Capacitor;

        OutFraction = Fraction(_outLine.Target, HitPointsScale);
        InFraction = Fraction(_inLine.Target, HitPointsScale);
        RepOutFraction = Fraction(_repOutLine.Target, HitPointsScale);
        CapOutFraction = Fraction(_capOutLine.Target, CapacitorScale);
        NeutOutFraction = Fraction(_neutOutLine.Target, CapacitorScale);
        NeutInFraction = Fraction(_neutInLine.Target, CapacitorScale);

        var netDamage = _inLine.Target - _repInLine.Target;
        IsUnderFire = IsUnderFire ? netDamage >= UnderFireOff : netDamage > UnderFireOn;
        IsNeuted = IsNeuted ? _neutInLine.Target >= NeutedOff : _neutInLine.Target >= NeutedOn;

        InDetail = RepIn > 0 ? $"reps {RepIn:N0} · net {SignedNet(RepIn - Received)}" : null;
        NeutInDetail = CapIn > 0 ? $"cap in {CapIn:N0}" : null;

        _hasDealt |= Dealt > 0;
        _hasRepaired |= RepOut > 0;
        _hasTransferredCap |= CapOut > 0;
        _hasNeutralized |= NeutOut > 0;
        ShowsRepOut = _hasRepaired;
        ShowsCapOut = _hasTransferredCap;
        ShowsNeutOut = _hasNeutralized;
        ShowsOut = _hasDealt || !(_hasRepaired || _hasTransferredCap || _hasNeutralized);
    }

    // Rounded to half a percent, so a bar that has not visibly moved raises no change.
    private static double Fraction(double value, double scale) =>
        scale <= 0 ? 0 : Math.Round(Math.Clamp(value / scale, 0, 1) * 200) / 200;

    private static string SignedNet(long net) => net switch
    {
        > 0 => $"+{net:N0}",
        < 0 => $"−{-net:N0}",
        _ => "0",
    };

    /// <summary>Mark the newest column with an event tick (e.g. a miss-burst or a scramble/jam notify).</summary>
    public void AddMarker(IBrush brush)
    {
        _markers.Add(new GraphMarker(0, brush));
        GraphRevision++;
    }

    private void AgeMarkers()
    {
        for (var i = _markers.Count - 1; i >= 0; i--)
        {
            var aged = _markers[i] with { Age = _markers[i].Age + 1 };
            if (aged.Age >= GraphCapacityValue)
                _markers.RemoveAt(i);
            else
                _markers[i] = aged;
        }
    }

    private static void Append(DpsSeries series, double value) => series.Add(value);

    // One live quantity: its series + smoothing state, keyed by the metric kind it renders. Target is the measured
    // rate (what the figures show); Ema is the smoothed line.
    private sealed class RateLine(MetricKind kind, IBrush ink, GraphLane lane, bool dashed = false)
    {
        public MetricKind Kind { get; } = kind;
        public DpsSeries Series { get; } = new(ink, GraphCapacityValue, lane, dashed);
        public double Ema;
        public double Target;
    }
}
