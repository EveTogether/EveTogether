using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What the distribution drawing needs, all as fractions of its width on one log scale: the heights of its bars
/// (0 to 1), where the typical run, the band and the long-run average fall, and the pilot's own runs as dots.</summary>
public sealed record EarningsDistribution(IReadOnlyList<double> Bars, double BandStart, double BandEnd, double TypicalMark,
    double? MeanMark, IReadOnlyList<double> OwnMarks);

/// <summary>One of the best or worst runs in the detail: the ISK, whose it is, how long it took and when. The pilot's own run
/// opens; another pilot's shows nothing that could name them (ET-512 decision 2).</summary>
public sealed partial class ActivityRunLineViewModel(string iskText, string whoText, string metaText, string? activityRef,
    DateTime completedAtUtc, Func<string, DateTime, Task>? open) : ViewModelBase
{
    public string IskText { get; } = iskText;

    public string WhoText { get; } = whoText;

    public string MetaText { get; } = metaText;

    public bool IsOwn { get; } = activityRef is not null && open is not null;

    [RelayCommand]
    private Task OpenAsync() => activityRef is not null && open is not null ? open(activityRef, completedAtUtc) : Task.CompletedTask;
}

/// <summary>
/// The detail of one activity's earnings, in the pane beside the list and the drawer over it (ET-517): the typical run,
/// its band and ISK/hour, the distribution on a log scale with the pilot's own runs marked, the three best and worst
/// runs, the long-run average with the jackpot share (from 30 runs), and what it is all based on. The figure is the
/// row's: a server's when there is one, else the pilot's own runs, and the source chip says which and switches between
/// servers when several answered.
///
/// <para>TOP DROPS is held back until the loot table exists (ET-519); that ticket gives it content and flips
/// <see cref="IsTopDropsShown"/>.</para>
/// </summary>
public sealed partial class ActivityStatsDetailViewModel(ISharedServerPicks? picks = null, Func<string, DateTime, Task>? openRun = null,
    TimeProvider? time = null) : ViewModelBase
{
    private const int DistributionBins = 16;
    private const int MaximumOwnDots = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ActivityStatsRowViewModel? _row;

    [ObservableProperty] private bool _hasRow;
    [ObservableProperty] private string _kindText = string.Empty;
    [ObservableProperty] private string _nameText = string.Empty;
    [ObservableProperty] private string _familyText = string.Empty;
    [ObservableProperty] private string _sourceChip = string.Empty;
    [ObservableProperty] private bool _isSharedSource;
    [ObservableProperty] private bool _hasFigures;
    [ObservableProperty] private bool _isTooFew;
    [ObservableProperty] private string _tooFewText = string.Empty;
    [ObservableProperty] private string _typicalText = string.Empty;
    [ObservableProperty] private string _bandLabel = string.Empty;
    [ObservableProperty] private string _bandText = string.Empty;
    [ObservableProperty] private string _perHourText = string.Empty;
    [ObservableProperty] private EarningsDistribution? _distribution;
    [ObservableProperty] private string _scaleMinText = string.Empty;
    [ObservableProperty] private string _scaleMaxText = string.Empty;
    [ObservableProperty] private bool _hasOwnDots;
    [ObservableProperty] private string _longRunText = string.Empty;
    [ObservableProperty] private string _longRunHint = string.Empty;
    [ObservableProperty] private bool _hasLongRunAverage;
    [ObservableProperty] private string _footerText = string.Empty;
    [ObservableProperty] private string _noteText = string.Empty;
    [ObservableProperty] private string _confidenceText = string.Empty;
    [ObservableProperty] private bool _isServerListOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HowToggleText))]
    private bool _isHowOpen;

    public string HowToggleText => IsHowOpen ? "How is this calculated? ▾" : "How is this calculated? ›";

    public bool HasFamily => FamilyText.Length > 0;

    public bool HasNote => NoteText.Length > 0;

    public bool HasConfidenceText => ConfidenceText.Length > 0;

    public bool HasPerHour => PerHourText.Length > 0;

    public bool HasServerChoice => ServerOptions.Count > 1;

    public bool ShowsPlainSourceChip => HasRow && !HasServerChoice;

    public bool ShowsChoiceSourceChip => HasRow && HasServerChoice;

    /// <summary>TOP DROPS area of the mockup. Nothing to show until the loot table exists (ET-519), so it is not on screen.</summary>
    public bool IsTopDropsShown => false;

    /// <summary>The LOOT TABLE button of the mockup, held back with TOP DROPS.</summary>
    public bool IsLootTableShown => false;

    public ActivityStatsKey? Key => _row?.Key;

    public ObservableCollection<ServerOptionViewModel> ServerOptions { get; } = [];

    public ObservableCollection<ActivityRunLineViewModel> BestRuns { get; } = [];

    public ObservableCollection<ActivityRunLineViewModel> WorstRuns { get; } = [];

    /// <summary>One dot per run needed for a number, filled for each run there is.</summary>
    public ObservableCollection<bool> RunDots { get; } = [];

    public void Show(ActivityStatsRowViewModel? row)
    {
        _row = row;
        OnPropertyChanged(nameof(Key));
        if (row is null)
        {
            HasRow = false;
            _NotifyChoice();
            return;
        }

        HasRow = true;
        KindText = $"EARNINGS · {row.TypeText.ToUpperInvariant()}";
        NameText = row.NameText;
        FamilyText = row.Label.FamilyText;
        OnPropertyChanged(nameof(HasFamily));
        _RenderServers(row);
        if (row.Basis is not { } basis)
        {
            _RenderTooFew(row.Stats.EffectiveRuns, row);
            return;
        }

        SourceChip = basis.Basis is EarningsBasis.AllPilots ? $"ALL PILOTS · {basis.ServerName}" : "YOUR RUNS";
        IsSharedSource = basis.Basis is EarningsBasis.AllPilots;
        NoteText = basis.Basis is EarningsBasis.YourRuns ? OwnRunsReasonText.For(row.Stats.Reason) : string.Empty;
        OnPropertyChanged(nameof(HasNote));
        FooterText = _FooterOf(basis, row.Stats.ReadAtUtc);
        if (basis.Figures.Typical is not { } typical)
        {
            _RenderTooFew(basis.Figures.EffectiveRunCount, row);
            return;
        }

        _RenderFigures(basis, typical, row);
    }

    private void _RenderFigures(ExpectedEarnings basis, decimal typical, ActivityStatsRowViewModel row)
    {
        EarningsFiguresDto figures = basis.Figures;
        HasFigures = true;
        IsTooFew = false;
        decimal low = (figures.Band is EarningsBand.Deciles ? figures.P10 : figures.P25) ?? typical;
        decimal high = (figures.Band is EarningsBand.Deciles ? figures.P90 : figures.P75) ?? typical;
        TypicalText = IskFormat.Compact(typical);
        BandLabel = figures.Band is EarningsBand.Deciles ? "8 OF 10 RUNS" : "HALF OF RUNS";
        BandText = $"{IskFormat.Compact(low)} – {IskFormat.Compact(high)}";
        PerHourText = figures.TypicalIskPerHour is { } perHour ? $"{IskFormat.Compact(perHour)}/h" : string.Empty;
        OnPropertyChanged(nameof(HasPerHour));
        ConfidenceText = figures.Confidence is EarningsConfidence.Low ? "Low confidence: fewer than 20 runs" : string.Empty;
        OnPropertyChanged(nameof(HasConfidenceText));

        // Dots: the pilot's own runs, which are the whole set when the figure is theirs.
        IReadOnlyList<EarningsYieldDto> ownYields = basis.Basis is EarningsBasis.YourRuns
            ? figures.Yields
            : row.Stats.Own?.Figures.Yields ?? [];
        decimal[] all = [.. figures.Yields.Concat(ownYields).Select(yield => yield.YieldIsk)];
        decimal worst = all.Length > 0 ? all.Min() : figures.Worst?.YieldIsk ?? low;
        decimal best = all.Length > 0 ? all.Max() : figures.Best?.YieldIsk ?? high;
        var scale = new ExpectedEarningsViewModel.LogScale(worst, best);
        double[] bars = new double[DistributionBins];
        foreach (EarningsYieldDto yield in figures.Yields)
            bars[Math.Min(DistributionBins - 1, (int)Math.Floor(scale.Position(yield.YieldIsk) * DistributionBins))] += yield.Weight;
        double tallest = bars.Max();
        double[] own = [.. ownYields.Take(MaximumOwnDots).Select(yield => scale.Position(yield.YieldIsk))];
        Distribution = new EarningsDistribution([.. bars.Select(bar => tallest > 0 ? bar / tallest : 0d)], scale.Position(low),
            scale.Position(high), scale.Position(typical), figures.LongRunAverage is { } mean ? scale.Position(mean) : null, own);
        HasOwnDots = own.Length > 0;
        ScaleMinText = IskFormat.Compact(worst);
        ScaleMaxText = IskFormat.Compact(best);

        BestRuns.ReconcileTo([.. figures.BestRuns.Select(_LineOf)]);
        WorstRuns.ReconcileTo([.. figures.WorstRuns.Select(_LineOf)]);
        _RenderLongRun(figures);
    }

    private void _RenderLongRun(EarningsFiguresDto figures)
    {
        HasLongRunAverage = figures.LongRunAverage is not null;
        if (figures.LongRunAverage is not { } mean)
        {
            LongRunText = string.Empty;
            LongRunHint = $"Shown from {EarningsPolicy.Default.LongRunAverageRuns.ToString("0", CultureInfo.InvariantCulture)} runs (now {Math.Floor(figures.EffectiveRunCount).ToString("0", CultureInfo.InvariantCulture)}). Below that, a few rare drops swing it too much to be useful.";
            return;
        }

        LongRunText = $"{IskFormat.Compact(mean)} ISK";
        double jackpots = Math.Round((figures.JackpotShare ?? 0d) * figures.EffectiveRunCount);
        LongRunHint = jackpots < 1
            ? "incl. rare drops. No run had a drop over 3× typical."
            : $"incl. rare drops. About {jackpots.ToString("0", CultureInfo.InvariantCulture)} of {Math.Round(figures.EffectiveRunCount).ToString("0", CultureInfo.InvariantCulture)} runs had a drop over 3× typical — that is why the average sits above the typical run.";
    }

    private void _RenderTooFew(double effectiveRuns, ActivityStatsRowViewModel row)
    {
        HasFigures = false;
        IsTooFew = true;
        int minimum = (int)EarningsPolicy.Default.MinimumRuns;
        int have = (int)Math.Min(minimum - 1, Math.Floor(effectiveRuns));
        RunDots.Clear();
        for (int dot = 0; dot < minimum; dot++)
            RunDots.Add(dot < have);
        TooFewText = $"Not enough runs yet — {have.ToString(CultureInfo.InvariantCulture)} of {minimum.ToString(CultureInfo.InvariantCulture)} needed";
        if (row.Basis is null)
        {
            SourceChip = "NO FIGURE";
            IsSharedSource = false;
            NoteText = OwnRunsReasonText.For(row.Stats.Reason);
            FooterText = string.Empty;
        }

        OnPropertyChanged(nameof(HasNote));
        BestRuns.Clear();
        WorstRuns.Clear();
        Distribution = null;
        HasOwnDots = false;
        HasLongRunAverage = false;
        PerHourText = string.Empty;
        OnPropertyChanged(nameof(HasPerHour));
    }

    private ActivityRunLineViewModel _LineOf(EarningsRunDto run) =>
        new($"{IskFormat.Compact(run.YieldIsk)} ISK", run.ActivityRef is not null ? "your run ›" : "another pilot",
            ExpectedEarningsViewModel.RunMeta(run), run.ActivityRef, run.CompletedAtUtc, openRun);

    private string _FooterOf(ExpectedEarnings basis, DateTime readAtUtc)
    {
        EarningsFiguresDto figures = basis.Figures;
        string window = figures.Window is EarningsWindow.Extended ? "older data · last 365 days" : "last 90 days";
        string based = basis.Basis is EarningsBasis.AllPilots
            ? $"{figures.RunCount.ToString(CultureInfo.InvariantCulture)} runs · {basis.PilotCount.ToString(CultureInfo.InvariantCulture)} pilots"
            : $"{figures.RunCount.ToString(CultureInfo.InvariantCulture)} of your runs";
        return $"Based on {based} · {window} · updated {OutcomeSharingServerViewModel.Age(_time.GetUtcNow().UtcDateTime - readAtUtc)}";
    }

    /// <summary>The source chip: opens the list of servers to switch between.</summary>
    [RelayCommand]
    private void ToggleServerList()
    {
        if (HasServerChoice)
            IsServerListOpen = !IsServerListOpen;
    }

    [RelayCommand]
    private void ToggleHow() => IsHowOpen = !IsHowOpen;

    private async Task _ChooseServerAsync(ExpectedEarnings answer)
    {
        if (_row is not { } row || answer.ServerAddress is not { } address)
            return;

        IsServerListOpen = false;
        row.UseBasis(answer);
        Show(row);
        if (picks is not null)
            await picks.SetAsync(address);
    }

    private void _RenderServers(ActivityStatsRowViewModel row)
    {
        ServerOptions.ReconcileTo([.. row.Stats.Answers.Select(answer => new ServerOptionViewModel(answer,
            answer.ServerAddress == row.Basis?.ServerAddress, _ChooseServerAsync))]);
        if (!HasServerChoice)
            IsServerListOpen = false;
        _NotifyChoice();
    }

    private void _NotifyChoice()
    {
        OnPropertyChanged(nameof(HasServerChoice));
        OnPropertyChanged(nameof(ShowsPlainSourceChip));
        OnPropertyChanged(nameof(ShowsChoiceSourceChip));
    }
}
