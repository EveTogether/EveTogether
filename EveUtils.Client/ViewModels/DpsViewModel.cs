using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Controls;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Dtos;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Live combat graph for ONE character (yourself or a fleet member). Beyond the primary DPS out/in lines it carries
/// extra live cap-warfare lines (neut, cap) — and any future combat metric is one entry in <see cref="_extraLines"/>
/// . Every line renders through the same shared path (<see cref="DpsRenderDriver"/> → <see cref="StepEma"/>), so
/// the own meters and the fleet-member meters stay identical.
/// </summary>
public partial class DpsViewModel : ViewModelBase
{
    private const int GraphCapacityValue = 9000;  // ~5min at 30fps — max retained history; the graph draws a fixed
                                                   // pixels-per-second slice of it anchored right, so a wider graph
                                                   // shows a longer timeline (covers fullscreen/ultrawide). Cheap: a
                                                   // ring buffer, and render only walks the visible samples
    private const double Smoothing = 0.15;        // EMA coefficient for the 30fps render path

    // Dealt = the current faction's bright accent; received stays semantic red. These two stay
    // first-class (the OUT/IN figures + the raw Apply path bind to them); the rest are generic extra lines.
    private readonly DpsSeries _dealtSeries = new(FactionAccentStroke(), GraphCapacityValue);
    private readonly DpsSeries _receivedSeries = new(new SolidColorBrush(Color.Parse("#FFEF5A5A")), GraphCapacityValue);

    // Extra live combat lines, registry-style: add a kind + colour here and it renders everywhere (graph + legend).
    private readonly RateLine _neutLine = new(MetricKind.Neut, Color.Parse("#FFB07EE0"), GraphCapacityValue); // purple
    private readonly RateLine _capLine = new(MetricKind.Cap, Color.Parse("#FF4D90FF"), GraphCapacityValue);   // blue (kept clear of the accent green)
    private readonly IReadOnlyList<RateLine> _extraLines;

    private static IBrush FactionAccentStroke() =>
        Application.Current?.TryGetResource("AccentBrightBrush", null, out var b) == true && b is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse("#FF7EE0BB"));
    private readonly List<GraphMarker> _markers = [];

    private double _emaDealt;
    private double _emaReceived;
    private double _targetDealt;
    private double _targetReceived;

    // A local meter pulls its live (decaying) rates from the gamelog each frame; a remote/fleet meter has no sampler
    // and is fed each line's latest value via SetRate. Either way the render frame smooths toward the target the same way.
    private Func<CombatRates?>? _sampler;

    [ObservableProperty] private long _dealt;
    [ObservableProperty] private long _received;
    [ObservableProperty] private long _neut;
    [ObservableProperty] private long _cap;
    [ObservableProperty] private string _character = "—";
    [ObservableProperty] private bool _isSelf;
    [ObservableProperty] private int _graphRevision;

    /// <summary>Whether an EVE client for this character is running on this machine (home dashboard presence dot);
    /// set best-effort by the dashboard from <c>EveClientPresenceService</c>.</summary>
    [ObservableProperty] private bool _inEve;

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

    /// <summary>The member's current solar system (fleet metrics, when location is shared); null = unknown/not shared.</summary>
    [ObservableProperty] private string? _location;

    /// <summary>The member's cumulative session bounty (fleet metrics, when bounty is shared). 0 = none/not shared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BountyText))]
    [NotifyPropertyChangedFor(nameof(HasBounty))]
    private long _bounty;

    /// <summary>The bounty compacted for display (e.g. 12.4M ISK).</summary>
    public string BountyText => CompactIsk(Bounty);

    /// <summary>Whether to show the bounty line (a member shares bounty and has earned some).</summary>
    public bool HasBounty => Bounty > 0;

    internal static string CompactIsk(long isk) =>
        isk >= 1_000_000_000 ? (isk / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B ISK"
        : isk >= 1_000_000 ? (isk / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M ISK"
        : isk >= 1_000 ? (isk / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k ISK"
        : isk + " ISK";

    /// <summary>Design-time constructor (XAML previewer).</summary>
    public DpsViewModel()
    {
        _extraLines = [_neutLine, _capLine];
        Series = [_dealtSeries, _receivedSeries, _neutLine.Series, _capLine.Series];
    }

    public DpsViewModel(string character, bool isSelf) : this()
    {
        Character = character;
        IsSelf = isSelf;
    }

    public IReadOnlyList<DpsSeries> Series { get; }

    /// <summary>Event markers on the time axis; ages tracked in lockstep with the scrolling series.</summary>
    public IReadOnlyList<GraphMarker> Markers => _markers;

    /// <summary>Apply a sample — call on the UI thread. Ages existing markers one column. Raw (no EMA); used by
    /// the event-driven remote-member path.</summary>
    public void Apply(DpsSampleDto sample)
    {
        Dealt = sample.DealtPerSecond;
        Received = sample.ReceivedPerSecond;
        Append(_dealtSeries, sample.DealtPerSecond);
        Append(_receivedSeries, sample.ReceivedPerSecond);
        AgeMarkers();
        GraphRevision++;
    }

    /// <summary>Timer-driven smoothed sample: an EMA over the windowed DPS rounds the curve the way the
    /// demo's 30fps render loop does, instead of plotting the raw stepped value. Shares the render core with the
    /// fleet path via <see cref="StepEma"/>.</summary>
    public void ApplySmoothed(DpsSampleDto sample)
    {
        _targetDealt = sample.DealtPerSecond;
        _targetReceived = sample.ReceivedPerSecond;
        StepEma();
    }

    /// <summary>Feed a local meter from the gamelog each frame: the render driver calls the sampler, which returns the
    /// current decaying rates (or null when this character has no live local tracker yet — then the frame is skipped
    /// and the event-driven remote path owns the series).</summary>
    public void UseSampler(Func<CombatRates?> sampler) => _sampler = sampler;

    /// <summary>Set one line's latest value for a remote (fleet) meter; the shared driver smooths toward it each frame,
    /// so a fleet graph scrolls and decays exactly like the own pop-out instead of stepping at 1 Hz. Unknown kinds are
    /// ignored (a newer client's metric kind degrades gracefully).</summary>
    public void SetRate(MetricKind kind, double value)
    {
        switch (kind)
        {
            case MetricKind.Dps:
                _targetDealt = value;
                break;
            case MetricKind.DpsIn:
                _targetReceived = value;
                break;
            default:
                foreach (var line in _extraLines)
                    if (line.Kind == kind)
                        line.Target = value;
                break;
        }
    }

    /// <summary>One render frame, driven by the shared <see cref="DpsRenderDriver"/> for every graph. A local meter
    /// refreshes its targets from the sampler first; a remote meter uses the last <see cref="SetRate"/> values.</summary>
    public void RenderFrame()
    {
        if (_sampler is not null)
        {
            var sample = _sampler();
            if (sample is null)
                return; // remote character on the main list — its event-driven Apply path owns the series
            _targetDealt = sample.Value.Dealt;
            _targetReceived = sample.Value.Received;
            _neutLine.Target = sample.Value.Neut;
            _capLine.Target = sample.Value.Cap;
        }

        StepEma();
    }

    // The single smoothing + scroll core shared by every combat graph (own + fleet): EMA every line toward its target,
    // append a frame, age the markers. A tweak here lands on all graphs and all lines at once.
    private void StepEma()
    {
        _emaDealt += Smoothing * (_targetDealt - _emaDealt);
        _emaReceived += Smoothing * (_targetReceived - _emaReceived);
        Dealt = (long)_emaDealt;
        Received = (long)_emaReceived;
        Append(_dealtSeries, _emaDealt);
        Append(_receivedSeries, _emaReceived);

        foreach (var line in _extraLines)
        {
            line.Ema += Smoothing * (line.Target - line.Ema);
            Append(line.Series, line.Ema);
        }

        Neut = (long)_neutLine.Ema;
        Cap = (long)_capLine.Ema;
        AgeMarkers();
        GraphRevision++;
    }

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

    // One extra live line: its own series + smoothing state, keyed by the metric kind it renders.
    private sealed class RateLine(MetricKind kind, Color colour, int capacity)
    {
        public MetricKind Kind { get; } = kind;
        public DpsSeries Series { get; } = new(new SolidColorBrush(colour), capacity);
        public double Ema;
        public double Target;
    }
}
