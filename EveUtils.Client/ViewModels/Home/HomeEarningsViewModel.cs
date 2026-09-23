using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Calendar;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>What the earnings block is drawn from: the activities of the read window and when tracking started.</summary>
public sealed record HomeEarningsInput(
    IReadOnlyList<RunsActivityFacts> Activities,
    DateTime NowLocal,
    DayOfWeek FirstDay,
    DateOnly? FirstTracked);

/// <summary>
/// EARNINGS (ET-324, the approved "3.A"): TODAY | THIS WEEK | this month as three tiles, and the last 30 days as bars.
/// Nothing here reads — the home's one runs read hands it the facts, and a click opens the runs overview on that range.
/// </summary>
public sealed partial class HomeEarningsViewModel : ObservableObject
{
    public const double ChartHeight = 70;

    private HomeEarningsInput? _input;

    public HomeEarningsViewModel(Action<EarningsPeriodKind, DateOnly> openRange)
    {
        Today = new HomeEarningsTileViewModel(EarningsPeriodKind.Today, openRange);
        Week = new HomeEarningsTileViewModel(EarningsPeriodKind.Week, openRange);
        Month = new HomeEarningsTileViewModel(EarningsPeriodKind.Month, openRange);
        Days = [.. Enumerable.Range(0, EarningsPeriods.ChartDays).Select(_ => new HomeEarningsDayViewModel(day => openRange(EarningsPeriodKind.Today, day)))];
    }

    public HomeEarningsTileViewModel Today { get; }

    public HomeEarningsTileViewModel Week { get; }

    public HomeEarningsTileViewModel Month { get; }

    /// <summary>Oldest first, today last — made once and told what they say.</summary>
    public IReadOnlyList<HomeEarningsDayViewModel> Days { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIskShade))]
    [NotifyPropertyChangedFor(nameof(IsRunsShade))]
    private RunsStripShade _shade = RunsStripShade.Isk;

    public bool IsIskShade => Shade == RunsStripShade.Isk;

    public bool IsRunsShade => Shade == RunsStripShade.Runs;

    [ObservableProperty] private string _chartMaxText = string.Empty;
    [ObservableProperty] private string _averageText = string.Empty;
    [ObservableProperty] private double _averageOffset;
    [ObservableProperty] private bool _hasAverage;

    [RelayCommand]
    private void ShadeByIsk() => _SetShade(RunsStripShade.Isk);

    [RelayCommand]
    private void ShadeByRuns() => _SetShade(RunsStripShade.Runs);

    public void Show(HomeEarningsInput input)
    {
        _input = input;
        Today.Show(EarningsPeriods.For(EarningsPeriodKind.Today, input.Activities, input.NowLocal, input.FirstDay, input.FirstTracked), input);
        Week.Show(EarningsPeriods.For(EarningsPeriodKind.Week, input.Activities, input.NowLocal, input.FirstDay, input.FirstTracked), input);
        Month.Show(EarningsPeriods.For(EarningsPeriodKind.Month, input.Activities, input.NowLocal, input.FirstDay, input.FirstTracked), input);
        _ShowChart(input);
    }

    private void _SetShade(RunsStripShade shade)
    {
        if (Shade == shade)
            return;

        Shade = shade;
        if (_input is { } input)
            _ShowChart(input);
    }

    private void _ShowChart(HomeEarningsInput input)
    {
        DateOnly today = DateOnly.FromDateTime(input.NowLocal);
        DateOnly weekStart = WeekMath.StartOf(today, input.FirstDay);
        DateOnly first = today.AddDays(-(EarningsPeriods.ChartDays - 1));
        ILookup<DateOnly, RunsActivityFacts> byDay = input.Activities
            .Where(activity => activity.Day >= first && activity.StartedAtLocal <= input.NowLocal)
            .ToLookup(activity => activity.Day);

        decimal[] values = [.. Enumerable.Range(0, EarningsPeriods.ChartDays)
            .Select(offset => RunsActivityStripViewModel.ValueOf(Shade, [.. byDay[first.AddDays(offset)]]))];
        decimal max = Math.Max(values.Max(), 0m);
        DateOnly[] tracked = [.. Enumerable.Range(0, EarningsPeriods.ChartDays)
            .Select(offset => first.AddDays(offset))
            .Where(day => input.FirstTracked is { } start && day >= start)];
        decimal average = tracked.Length == 0 ? 0m : tracked.Sum(day => values[day.DayNumber - first.DayNumber]) / tracked.Length;

        for (int offset = 0; offset < EarningsPeriods.ChartDays; offset++)
        {
            DateOnly day = first.AddDays(offset);
            bool isTracked = input.FirstTracked is { } start && day >= start;
            Days[offset].Show(day, max == 0 ? 0 : (double)(values[offset] / max) * ChartHeight, isTracked,
                day == today ? EarningsBarAge.Today : day >= weekStart ? EarningsBarAge.ThisWeek : EarningsBarAge.Older,
                day == first || day.DayOfWeek == input.FirstDay ? _DayLabel(day) : string.Empty,
                isTracked ? RunsActivityStripViewModel.DayTooltip(day, [.. byDay[day]]) : $"{_DayLabel(day)} · not tracked yet");
        }

        ChartMaxText = max == 0 ? string.Empty : _ValueText(max);
        HasAverage = tracked.Length > 0 && average > 0;
        AverageOffset = max == 0 ? 0 : (double)(average / max) * ChartHeight;
        AverageText = HasAverage ? $"avg {_ValueText(average)} per tracked day" : string.Empty;
    }

    private string _ValueText(decimal value) =>
        Shade == RunsStripShade.Isk ? IskFormat.Compact(value) : value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string _DayLabel(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);
}

public enum EarningsBarAge
{
    Older,
    ThisWeek,
    Today
}

/// <summary>One of the 30 bars: its height in pixels, how recent it is (today, this week or older — the three
/// strengths of the accent), and whether tracking had started by then.</summary>
public sealed partial class HomeEarningsDayViewModel(Action<DateOnly> open) : ObservableObject
{
    public DateOnly Date { get; private set; }

    [ObservableProperty] private double _barHeight;
    [ObservableProperty] private bool _isTracked;
    [ObservableProperty] private EarningsBarAge _age;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _tooltip = string.Empty;

    /// <summary>The bars need 3:1 on the panel: accent-b at full, 76 % and 58 % measured 3.2:1 at the lowest (Minmatar,
    /// the older days) in the v1 contrast table.</summary>
    public double BarOpacity => Age switch
    {
        EarningsBarAge.Today => 1,
        EarningsBarAge.ThisWeek => 0.76,
        _ => 0.58
    };

    partial void OnAgeChanged(EarningsBarAge value) => OnPropertyChanged(nameof(BarOpacity));

    internal void Show(DateOnly date, double barHeight, bool isTracked, EarningsBarAge age, string label, string tooltip)
    {
        Date = date;
        BarHeight = barHeight;
        IsTracked = isTracked;
        Age = age;
        Label = label;
        Tooltip = tooltip;
    }

    [RelayCommand]
    private void Open()
    {
        if (IsTracked)
            open(Date);
    }
}

/// <summary>One earnings tile: own ISK large, then runs · time flown · ISK/h, then the change against the same point
/// in the previous period, then the ISK-by-source bar.</summary>
public sealed partial class HomeEarningsTileViewModel(EarningsPeriodKind kind, Action<EarningsPeriodKind, DateOnly> open)
    : ObservableObject
{
    private DateOnly _start;

    public EarningsPeriodKind Kind { get; } = kind;

    [ObservableProperty] private string _kickerText = string.Empty;
    [ObservableProperty] private string _rangeText = string.Empty;
    [ObservableProperty] private string _iskText = string.Empty;
    [ObservableProperty] private bool _hasIsk;
    [ObservableProperty] private string _factsText = string.Empty;
    [ObservableProperty] private string _perHourText = string.Empty;
    [ObservableProperty] private string _factsWithRateText = string.Empty;
    [ObservableProperty] private string _comparisonText = string.Empty;
    [ObservableProperty] private string _comparisonTooltip = string.Empty;
    [ObservableProperty] private IskBreakdown _sources = IskBreakdown.None;

    internal void Show(EarningsPeriodFigures figures, HomeEarningsInput input)
    {
        _start = figures.Start;
        KickerText = Kind switch
        {
            EarningsPeriodKind.Today => "TODAY",
            EarningsPeriodKind.Week => "THIS WEEK",
            _ => figures.Start.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant()
        };
        RangeText = _RangeText(figures, input);
        HasIsk = figures.Net.HasValue;
        IskText = figures.Net is { } net ? IskFormat.Compact(net) : "—";
        PerHourText = figures.PerHour is { } perHour ? IskFormat.Compact(perHour) + "/h" : string.Empty;
        FactsText = $"{figures.Runs} run{(figures.Runs == 1 ? "" : "s")} · {FlownText(figures.Flown)} flown";
        FactsWithRateText = PerHourText.Length == 0 ? FactsText : $"{FactsText} · {PerHourText}";
        Sources = figures.Sources;
        (ComparisonText, ComparisonTooltip) = _Comparison(figures, input.NowLocal);
    }

    [RelayCommand]
    private void Open() => open(Kind, _start);

    /// <summary>"1h 41m", "40h 11m" — hours keep counting past a day.</summary>
    public static string FlownText(TimeSpan flown) => $"{(int)flown.TotalHours}h {flown.Minutes}m";

    private static string _RangeText(EarningsPeriodFigures figures, HomeEarningsInput input)
    {
        DateOnly today = DateOnly.FromDateTime(input.NowLocal);
        string month = $"{figures.Start.Day}–{today.Day} {today.ToString("MMM", CultureInfo.InvariantCulture)}";
        return figures.Kind switch
        {
            EarningsPeriodKind.Today => today.ToString("ddd d MMM", CultureInfo.InvariantCulture),
            EarningsPeriodKind.Week => $"{WeekMath.RangeText(figures.Start)} · from {figures.Start.DayOfWeek}",
            _ => input.FirstTracked is { } tracked && tracked > figures.Start
                ? $"{month} · tracked since {tracked.ToString("d MMM", CultureInfo.InvariantCulture)}"
                : month
        };
    }

    private static (string Text, string Tooltip) _Comparison(EarningsPeriodFigures figures, DateTime nowLocal)
    {
        string previousName = figures.Kind switch
        {
            EarningsPeriodKind.Today => "last " + figures.PreviousStart.ToString("ddd", CultureInfo.InvariantCulture),
            EarningsPeriodKind.Week => "last week",
            _ => figures.PreviousStart.ToString("MMMM", CultureInfo.InvariantCulture)
        };
        string byWhen = figures.Kind == EarningsPeriodKind.Today
            ? "by " + nowLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "by now";

        if (figures.PreviousNet is not { } previous)
            return ($"{_Capitalised(previousName)}: not tracked", $"Nothing was recorded for {previousName}, so there is nothing to compare with.");

        decimal current = figures.Net ?? 0m;
        string tooltip = $"{_Capitalised(previousName)} {byWhen}: {IskFormat.Whole(previous)}\nNow: {IskFormat.Whole(current)}";
        if (previous == 0)
            return ($"{_Capitalised(previousName)} {byWhen}: nothing", tooltip);

        decimal change = (current - previous) / previous * 100m;
        string arrow = change >= 0 ? "▲" : "▼";
        return ($"{arrow} {Math.Abs(change):0}% vs {previousName} {byWhen}", tooltip);
    }

    private static string _Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
