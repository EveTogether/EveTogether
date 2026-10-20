using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// The EXPECTED EARNINGS panel under the run window's clock (ET-516): what this site typically yields, how far runs
/// stray from that, the real worst and best run, and where the current run stands on the same scale. Informational:
/// it opens by itself and asks for nothing.
///
/// <para>The figure of the pilot's own runs shows at once, and a server's figure replaces it once it answers (and only
/// then, so a slow or unreachable server costs the pilot nothing). Below the minimum of runs there is no number at all.
/// A fetch belongs to its key: switching to another site cancels the old one, so a late answer can never land on the
/// new site's panel.</para>
/// </summary>
public sealed partial class ExpectedEarningsViewModel(IExpectedEarningsSource source, ILogger<ExpectedEarningsViewModel>? logger = null)
    : ViewModelBase
{
    private const int MinimumRunsDots = 5;

    private readonly Dictionary<string, ExpectedEarnings> _cache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loading;
    private ActivityStatsKey? _key;
    private ExpectedEarnings? _shown;
    private OwnRunsReason _reason;
    private decimal? _currentIsk;
    private int _fleetSize = 1;
    private LogScale _scale = new(1, 1);

    [ObservableProperty] private ExpectedEarningsState _state = ExpectedEarningsState.Hidden;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _headerSummary = string.Empty;
    [ObservableProperty] private string _sourceChip = string.Empty;
    [ObservableProperty] private bool _isSharedSource;
    [ObservableProperty] private string _typicalText = string.Empty;
    [ObservableProperty] private string _perHourText = string.Empty;
    [ObservableProperty] private string _perPilotText = string.Empty;
    [ObservableProperty] private string _bandLabel = string.Empty;
    [ObservableProperty] private string _bandText = string.Empty;
    [ObservableProperty] private string _scaleMinText = string.Empty;
    [ObservableProperty] private string _scaleMaxText = string.Empty;
    [ObservableProperty] private string _worstText = string.Empty;
    [ObservableProperty] private string _worstMeta = string.Empty;
    [ObservableProperty] private string _bestText = string.Empty;
    [ObservableProperty] private string _bestMeta = string.Empty;
    [ObservableProperty] private string _basisText = string.Empty;
    [ObservableProperty] private string _confidenceText = string.Empty;
    [ObservableProperty] private string _noteText = string.Empty;
    [ObservableProperty] private string _tooFewText = string.Empty;
    [ObservableProperty] private double _bandStart;
    [ObservableProperty] private double _bandEnd;
    [ObservableProperty] private double _typicalMark;
    [ObservableProperty] private double? _currentMark;
    [ObservableProperty] private string _currentText = string.Empty;

    public bool IsShown => State is not ExpectedEarningsState.Hidden;
    public bool IsLoading => State is ExpectedEarningsState.Loading;
    public bool HasFigures => State is ExpectedEarningsState.Figures;
    public bool IsTooFewRuns => State is ExpectedEarningsState.TooFewRuns;
    public bool HasPerHour => PerHourText.Length > 0;
    public bool HasPerPilot => PerPilotText.Length > 0;
    public bool HasConfidenceText => ConfidenceText.Length > 0;
    public bool HasNote => NoteText.Length > 0;
    public bool HasCurrent => CurrentMark is not null;

    /// <summary>The LOOT TABLE button of the mockup. Nothing to open until the loot table exists (ET-519), so it is not on screen.</summary>
    public bool IsLootTableShown => false;

    /// <summary>One line for the compact views and the folded header: "Typical 38M · 9–97M".</summary>
    public string CompactLine { get; private set; } = string.Empty;

    /// <summary>One dot per run needed for a number, filled for each run there is.</summary>
    public ObservableCollection<bool> RunDots { get; } = [];

    public ActivityStatsKey? Key => _key;

    /// <summary>Points the panel at the activity the window is on, or at none. The same key again is left alone.</summary>
    public Task TrackAsync(ActivityStatsKey? key, int? characterId, string? activityRef = null)
    {
        if (key == _key)
            return Task.CompletedTask;

        _loading?.Cancel();
        _key = key;
        _shown = null;
        _reason = OwnRunsReason.None;
        if (key is null)
        {
            _Hide();
            return Task.CompletedTask;
        }

        _loading = new CancellationTokenSource();
        return _LoadAsync(key, characterId, activityRef, _loading.Token);
    }

    /// <summary>The group's TOTAL ISK right now, for the marker. Null while there is no figure to compare.</summary>
    public void UpdateCurrentIsk(decimal? totalIsk)
    {
        if (totalIsk == _currentIsk)
            return;

        _currentIsk = totalIsk;
        _RenderCurrent();
    }

    /// <summary>How many pilots fly the activity: the figures are its whole take, so a fleet also reads per pilot.</summary>
    public void UpdateFleetSize(int fleetSize)
    {
        if (fleetSize == _fleetSize)
            return;

        _fleetSize = Math.Max(1, fleetSize);
        if (_shown is not null)
            _Render();
    }

    private async Task _LoadAsync(ActivityStatsKey key, int? characterId, string? activityRef, CancellationToken cancellationToken)
    {
        try
        {
            if (_cache.TryGetValue(key.Code, out ExpectedEarnings? cached))
            {
                _Show(cached, OwnRunsReason.None);
                return;
            }

            State = ExpectedEarningsState.Loading;
            HeaderSummary = "Loading…";
            _Show(await source.LoadOwnAsync(key, cancellationToken), OwnRunsReason.None, cancellationToken);
            if (characterId is not { } character)
                return;

            SharedEarningsAttempt attempt = await source.LoadSharedAsync(key, character, cancellationToken, activityRef);
            if (attempt.Shared is { } shared)
                _Show(shared, OwnRunsReason.None, cancellationToken);
            else
                _SetReason(attempt.Reason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Expected earnings for {Key} could not be read", key.Code);
            if (!cancellationToken.IsCancellationRequested)
                _Hide();
        }
    }

    private void _Show(ExpectedEarnings earnings, OwnRunsReason reason, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        if (_key is { } key && earnings.Basis is EarningsBasis.AllPilots)
            _cache[key.Code] = earnings;
        _shown = earnings;
        _reason = reason;
        _Render();
    }

    private void _SetReason(OwnRunsReason reason, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _shown is null)
            return;

        _reason = reason;
        _Render();
    }

    private void _Hide()
    {
        State = ExpectedEarningsState.Hidden;
        HeaderSummary = string.Empty;
        CompactLine = string.Empty;
        OnPropertyChanged(nameof(CompactLine));
        _Notify();
    }

    private void _Render()
    {
        if (_shown is not { } earnings)
            return;

        EarningsFiguresDto figures = earnings.Figures;
        SourceChip = earnings.Basis is EarningsBasis.AllPilots ? $"ALL PILOTS · {earnings.ServerName}" : "YOUR RUNS";
        IsSharedSource = earnings.Basis is EarningsBasis.AllPilots;
        BasisText = _BasisLine(earnings);
        NoteText = _NoteFor(_reason);

        if (figures.Typical is not { } typical)
        {
            _RenderTooFew(figures);
            return;
        }

        State = ExpectedEarningsState.Figures;
        decimal low = (figures.Band is EarningsBand.Deciles ? figures.P10 : figures.P25) ?? typical;
        decimal high = (figures.Band is EarningsBand.Deciles ? figures.P90 : figures.P75) ?? typical;
        decimal worst = figures.Worst?.YieldIsk ?? low;
        decimal best = figures.Best?.YieldIsk ?? high;
        TypicalText = IskFormat.Compact(typical);
        PerHourText = figures.TypicalIskPerHour is { } perHour ? $"{IskFormat.Compact(perHour)}/h" : string.Empty;
        PerPilotText = _fleetSize > 1 ? $"per pilot ≈ {IskFormat.Compact(typical / _fleetSize)}" : string.Empty;
        BandLabel = figures.Band is EarningsBand.Deciles ? "8 OF 10 RUNS" : "HALF OF RUNS";
        BandText = $"{IskFormat.Compact(low)} – {IskFormat.Compact(high)}";
        ConfidenceText = figures.Confidence is EarningsConfidence.Low ? "Low confidence: fewer than 20 runs" : string.Empty;
        ScaleMinText = IskFormat.Compact(worst);
        ScaleMaxText = IskFormat.Compact(best);
        WorstText = figures.Worst is { } worstRun ? $"{IskFormat.Compact(worstRun.YieldIsk)} ISK" : string.Empty;
        WorstMeta = figures.Worst is { } ? _RunMeta(figures.Worst) : string.Empty;
        BestText = figures.Best is { } bestRun ? $"{IskFormat.Compact(bestRun.YieldIsk)} ISK" : string.Empty;
        BestMeta = figures.Best is { } ? _RunMeta(figures.Best) : string.Empty;
        _scale = new LogScale(worst, best);
        BandStart = _scale.Position(low);
        BandEnd = _scale.Position(high);
        TypicalMark = _scale.Position(typical);
        HeaderSummary = CompactLine = $"Typical {IskFormat.Compact(typical)} · {IskFormat.Compact(low)}–{IskFormat.Compact(high)}";
        OnPropertyChanged(nameof(CompactLine));
        _RenderCurrent();
        _Notify();
    }

    private void _RenderTooFew(EarningsFiguresDto figures)
    {
        State = ExpectedEarningsState.TooFewRuns;
        int have = (int)Math.Min(MinimumRunsDots - 1, Math.Floor(figures.EffectiveRunCount));
        RunDots.Clear();
        for (int dot = 0; dot < MinimumRunsDots; dot++)
            RunDots.Add(dot < have);
        TooFewText = $"Not enough runs yet — {have} of {MinimumRunsDots} needed";
        TypicalText = PerHourText = PerPilotText = BandLabel = BandText = ConfidenceText = string.Empty;
        HeaderSummary = "Not enough runs yet";
        CompactLine = string.Empty;
        OnPropertyChanged(nameof(CompactLine));
        CurrentMark = null;
        _Notify();
    }

    private void _RenderCurrent()
    {
        if (State is not ExpectedEarningsState.Figures || _currentIsk is not { } current || current <= 0)
        {
            CurrentMark = null;
            CurrentText = string.Empty;
            OnPropertyChanged(nameof(HasCurrent));
            return;
        }

        CurrentMark = _scale.Position(current);
        string beyond = current > _scale.High ? " ▲ above best" : current < _scale.Low ? " ▼ below worst" : string.Empty;
        CurrentText = $"THIS RUN +{IskFormat.Compact(current)}{beyond}";
        OnPropertyChanged(nameof(HasCurrent));
    }

    partial void OnStateChanged(ExpectedEarningsState value) => _Notify();

    private void _Notify()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasFigures));
        OnPropertyChanged(nameof(IsTooFewRuns));
        OnPropertyChanged(nameof(HasPerHour));
        OnPropertyChanged(nameof(HasPerPilot));
        OnPropertyChanged(nameof(HasConfidenceText));
        OnPropertyChanged(nameof(HasNote));
    }

    private static string _BasisLine(ExpectedEarnings earnings)
    {
        EarningsFiguresDto figures = earnings.Figures;
        string window = figures.Window is EarningsWindow.Extended ? "older data · last 365 days" : "last 90 days";
        return earnings.Basis is EarningsBasis.AllPilots
            ? $"based on {figures.RunCount} runs · {earnings.PilotCount} pilots · {window}"
            : $"based on {figures.RunCount} of your runs · {window}";
    }

    private static string _NoteFor(OwnRunsReason reason) => reason switch
    {
        OwnRunsReason.NotSharing => "Showing your own runs. Turn on sharing in Settings to include the other pilots' runs.",
        OwnRunsReason.TooFewPilots => "Too few pilots share this site yet, so this is your own runs.",
        OwnRunsReason.TooFewSharedRuns => "The pilots sharing this site have too few runs yet, so this is your own runs.",
        OwnRunsReason.ServerUnavailable => "The server could not be reached, so this is your own runs.",
        _ => string.Empty
    };

    private static string _RunMeta(EarningsRunDto run)
    {
        string day = run.CompletedAtUtc.ToString("d MMM", CultureInfo.InvariantCulture);
        return run.DurationSeconds is { } seconds ? $"{_Duration(seconds)} · {day}" : day;
    }

    private static string _Duration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60:00}m" : $"{Math.Max(1, seconds / 60)}m";

    /// <summary>Position on the bar, 0 to 1, on a log scale: a site's runs run from a few million to a jackpot hundreds
    /// of times that, and a linear bar would put every ordinary run at the left edge. A value outside the range sits
    /// on the edge.</summary>
    internal sealed class LogScale
    {
        private readonly double _low;
        private readonly double _high;

        public LogScale(decimal worst, decimal best)
        {
            _low = Math.Max(1d, (double)worst * 0.8);
            _high = Math.Max(_low * 1.01, (double)best * 1.1);
            Low = worst;
            High = best;
        }

        public decimal Low { get; }
        public decimal High { get; }

        public double Position(decimal value) =>
            Math.Clamp((Math.Log10(Math.Max((double)value, _low)) - Math.Log10(_low)) / (Math.Log10(_high) - Math.Log10(_low)), 0d, 1d);
    }
}
