using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Calendar;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Everything one drawing of the strip depends on. The days are the selected tab's own, counted from the
/// overview read (strip ∪ month in view) rather than from rows, of which only the month has any.</summary>
public sealed record RunsStripInput(
    DayOfWeek FirstDay,
    DateOnly Today,
    DateOnly? FirstTracked,
    DateOnly MonthInView,
    RunsRangeKind RangeKind,
    DateOnly RangeStart,
    IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> Days);

/// <summary>
/// The activity strip above the runs list (ET-292): twelve weeks of evenings as a grid of cells, GitHub-profile style,
/// shaded by ISK or by runs, with a bar of week segments under it. A click on a day or a week picks it for the range
/// line — it never filters the list (besluit Jithran, 15 Sep); the screen unfolds that day and scrolls it into view.
///
/// <para><b>Weeks come from <see cref="WeekMath"/> only.</b> The top row is the week's first day as the "Week starts
/// on" setting (ET-297) has it, and a change there re-lays the grid in place. Dates are <see cref="DateOnly"/>
/// throughout, so no week is ever seven times twenty-four hours.</para>
///
/// <para><b>Nothing is rebuilt.</b> The 84 cells, 12 segments and 12 month slots are made once and told what they now
/// say, so a live refresh or a week-start change moves no control.</para>
/// </summary>
public sealed partial class RunsActivityStripViewModel : ObservableObject
{
    public const int Weeks = 12;

    private readonly Action<DateOnly> _dayClicked;
    private readonly Action<DateOnly> _weekClicked;
    private readonly Action<DateOnly> _monthClicked;
    private RunsStripInput? _input;

    public RunsActivityStripViewModel(Action<DateOnly> dayClicked, Action<DateOnly> weekClicked, Action<DateOnly> monthClicked)
    {
        _dayClicked = dayClicked;
        _weekClicked = weekClicked;
        _monthClicked = monthClicked;
        // UniformGrid fills row by row, so the cells are handed over in that order: the week's first weekday across all
        // twelve weeks, then its second, and so on.
        Cells = [.. Enumerable.Range(0, 7 * Weeks).Select(_ => new RunsStripCellViewModel(_OnCellClicked))];
        WeekSegments = [.. Enumerable.Range(0, Weeks).Select(_ => new RunsStripWeekViewModel(_OnWeekClicked))];
        MonthLabels = [.. Enumerable.Range(0, Weeks).Select(_ => new RunsStripMonthLabelViewModel(_OnMonthClicked))];
        WeekdayLabels = [.. Enumerable.Range(0, 7).Select(_ => new RunsStripWeekdayLabel())];
    }

    public IReadOnlyList<RunsStripCellViewModel> Cells { get; }

    public IReadOnlyList<RunsStripWeekViewModel> WeekSegments { get; }

    public IReadOnlyList<RunsStripMonthLabelViewModel> MonthLabels { get; }

    public IReadOnlyList<RunsStripWeekdayLabel> WeekdayLabels { get; }

    /// <summary>The first day in the grid: top left.</summary>
    public DateOnly Start { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIskShade))]
    [NotifyPropertyChangedFor(nameof(IsRunsShade))]
    private RunsStripShade _shade = RunsStripShade.Isk;

    public bool IsIskShade => Shade == RunsStripShade.Isk;

    public bool IsRunsShade => Shade == RunsStripShade.Runs;

    partial void OnShadeChanged(RunsStripShade value)
    {
        if (_input is { } input)
            Show(input);
    }

    [RelayCommand]
    private void ShadeByIsk() => Shade = RunsStripShade.Isk;

    [RelayCommand]
    private void ShadeByRuns() => Shade = RunsStripShade.Runs;

    /// <summary>
    /// The week the strip ends on. Normally the week of today — paging back a month or two leaves the grid where it
    /// is, since that month is already in it. Only a month that starts before the grid's first week moves it: then
    /// it ends on the week of that month's last day, so the month the list shows is always in the strip too.
    /// </summary>
    public static DateOnly EndWeekFor(DateOnly today, DateOnly monthInView, DayOfWeek firstDay)
    {
        DateOnly todaysWeek = WeekMath.StartOf(today, firstDay);
        DateOnly start = todaysWeek.AddDays(-7 * (Weeks - 1));
        return monthInView >= start && monthInView <= today
            ? todaysWeek
            : WeekMath.StartOf(monthInView.AddMonths(1).AddDays(-1), firstDay);
    }

    public static DateOnly StartFor(DateOnly today, DateOnly monthInView, DayOfWeek firstDay) =>
        EndWeekFor(today, monthInView, firstDay).AddDays(-7 * (Weeks - 1));

    public void Show(RunsStripInput input)
    {
        _input = input;
        Start = StartFor(input.Today, input.MonthInView, input.FirstDay);
        DateOnly monthEnd = input.MonthInView.AddMonths(1);

        IReadOnlyList<DayOfWeek> order = WeekMath.DaysInOrder(input.FirstDay);
        for (int row = 0; row < 7; row++)
            // Every other row is labelled, from the second: Tue/Thu/Sat under a Monday start, Mon/Wed/Fri under Sunday.
            WeekdayLabels[row].Text = row % 2 == 1
                ? CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(order[row])
                : string.Empty;

        Func<decimal, bool, StripLevel> levelOf = LevelScale([.. Enumerable.Range(0, 7 * Weeks)
            .Select(offset => _ValueOf(input, Start.AddDays(offset)))]);

        for (int week = 0; week < Weeks; week++)
        {
            DateOnly weekStart = Start.AddDays(7 * week);
            for (int row = 0; row < 7; row++)
            {
                DateOnly day = weekStart.AddDays(row);
                input.Days.TryGetValue(day, out IReadOnlyList<RunsActivityFacts>? facts);
                StripLevel level = levelOf(_ValueOf(input, day), facts is { Count: > 0 });
                Cells[row * Weeks + week].Show(day, level,
                    isFuture: day > input.Today,
                    isUntracked: day <= input.Today && (input.FirstTracked is not { } first || day < first) && facts is null,
                    isToday: day == input.Today,
                    isPicked: input.RangeKind == RunsRangeKind.Day && input.RangeStart == day,
                    isOutsideMonth: day < input.MonthInView || day >= monthEnd,
                    tooltip: DayTooltip(day, facts));
            }

            List<RunsActivityFacts> weekFacts = [.. Enumerable.Range(0, 7)
                .SelectMany(offset => input.Days.GetValueOrDefault(weekStart.AddDays(offset)) ?? [])];
            WeekSegments[week].Show(weekStart,
                isClickable: weekStart <= input.Today,
                isPicked: input.RangeKind == RunsRangeKind.Week && input.RangeStart == weekStart,
                tooltip: _Tooltip(WeekMath.RangeText(weekStart), weekFacts.Count > 0 ? weekFacts : null));
        }

        _LabelMonths(input);
    }

    /// <summary>A month is named over the first week that starts inside its first seven days (or over the first column,
    /// for the month the strip opens in), and never two names within three columns of each other — the later one wins,
    /// since that is the month the eye is heading towards.</summary>
    private void _LabelMonths(RunsStripInput input)
    {
        List<(int Week, DateOnly Month)> labels = [];
        for (int week = 0; week < Weeks; week++)
        {
            DateOnly weekStart = Start.AddDays(7 * week);
            var month = new DateOnly(weekStart.Year, weekStart.Month, 1);
            if ((weekStart.Day <= 7 || (week == 0 && labels.Count == 0)) && labels.All(label => label.Month != month))
                labels.Add((week, month));
        }

        for (int index = labels.Count - 1; index > 0; index--)
            if (labels[index].Week - labels[index - 1].Week < 3)
                labels.RemoveAt(index - 1);

        Dictionary<int, DateOnly> monthByWeek = labels.ToDictionary(label => label.Week, label => label.Month);
        for (int week = 0; week < Weeks; week++)
        {
            DateOnly? month = monthByWeek.TryGetValue(week, out DateOnly named) ? named : null;
            MonthLabels[week].Show(month, month == input.MonthInView);
        }
    }

    /// <summary>A diverging scale: gains step 1–4 at the quartiles of the gaining cells, so a quiet stretch still reads as
    /// light and heavy against itself instead of all of it drowning under one big evening, and losses step 1–4 by their
    /// own rank over |value|, so a lost billion never flattens the small losses beside it. A cell at zero is empty,
    /// or neutral where something happened that day. The summary's HOURS and DAYS (ET-294) step the same way.</summary>
    internal static Func<decimal, bool, StripLevel> LevelScale(IEnumerable<decimal> values)
    {
        decimal[] all = [.. values];
        Func<decimal, int> gainStep = _QuartileSteps(all.Where(value => value > 0));
        Func<decimal, int> lossStep = _RankSteps(all.Where(value => value < 0).Select(value => -value));
        return (value, hasActivity) => value switch
        {
            > 0 => new StripLevel(StripTone.Gain, gainStep(value)),
            < 0 => new StripLevel(StripTone.Loss, lossStep(-value)),
            _ => hasActivity ? StripLevel.Neutral : StripLevel.Empty
        };
    }

    /// <summary>By cumulative rank, so the biggest loss is always the strongest step: losses are rare, and the quartile
    /// thresholds above would leave a lone loss, however large, at the faintest step.</summary>
    private static Func<decimal, int> _RankSteps(IEnumerable<decimal> magnitudes)
    {
        decimal[] sorted = [.. magnitudes];
        return magnitude => Math.Clamp((int)Math.Ceiling(4.0 * sorted.Count(other => other <= magnitude) / sorted.Length), 1, 4);
    }

    private static Func<decimal, int> _QuartileSteps(IEnumerable<decimal> magnitudes)
    {
        decimal[] sorted = [.. magnitudes.Order()];
        decimal Quartile(double fraction) =>
            sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(fraction * sorted.Length))];
        decimal[] thresholds = [Quartile(0.25), Quartile(0.5), Quartile(0.75)];
        return magnitude => magnitude <= thresholds[0] ? 1 : magnitude <= thresholds[1] ? 2 : magnitude <= thresholds[2] ? 3 : 4;
    }

    /// <summary>What a set of activities weighs under a shade: their own share's ISK, or how many there are.</summary>
    internal static decimal ValueOf(RunsStripShade shade, IReadOnlyCollection<IRunsActivityFigures> activities) =>
        shade == RunsStripShade.Runs
            ? activities.Count
            : activities.Where(activity => activity.NetIsk.HasValue).Sum(activity => activity.NetIsk!.Value);

    private decimal _ValueOf(RunsStripInput input, DateOnly day) =>
        input.Days.TryGetValue(day, out IReadOnlyList<RunsActivityFacts>? facts) ? ValueOf(Shade, facts) : 0;

    /// <summary>A day's tooltip, the one the summary's DAYS (ET-294) repeats for the same day.</summary>
    internal static string DayTooltip(DateOnly day, IReadOnlyList<RunsActivityFacts>? facts) =>
        _Tooltip(day.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant(), facts);

    /// <summary>The same three phrases the day header says about these activities, word for word.</summary>
    private static string _Tooltip(string when, IReadOnlyList<RunsActivityFacts>? facts) =>
        facts is null || facts.Count == 0
            ? $"{when} · no runs"
            : $"{when} · {RunsActivitySummaryText.ActivitiesCount(facts.Count)} · {RunsActivitySummaryText.FlownFor(facts)} · {RunsActivitySummaryText.NetFor(facts)}";

    private void _OnCellClicked(RunsStripCellViewModel cell)
    {
        if (cell.IsClickable)
            _dayClicked(cell.Date);
    }

    private void _OnWeekClicked(RunsStripWeekViewModel week)
    {
        if (week.IsClickable)
            _weekClicked(week.WeekStart);
    }

    private void _OnMonthClicked(RunsStripMonthLabelViewModel label)
    {
        if (label.Month is { } month)
            _monthClicked(month);
    }
}

/// <summary>A day in the strip. Its fill and its edges are separate on purpose: the shade and "outside the month" are an
/// opacity on the fill, and an opacity on the whole cell would dim today's edge and the picked outline with it.</summary>
public sealed partial class RunsStripCellViewModel(Action<RunsStripCellViewModel> clicked) : ObservableObject
{
    internal static readonly double[] LevelOpacity = [1, .28, .48, .70, 1];

    public DateOnly Date { get; private set; }

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isAccent;
    [ObservableProperty] private bool _isBright;
    [ObservableProperty] private bool _isNeutral;
    [ObservableProperty] private bool _isLoss;
    [ObservableProperty] private bool _isLossBright;
    [ObservableProperty] private bool _isFuture;
    [ObservableProperty] private bool _isUntracked;
    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private bool _isPicked;
    [ObservableProperty] private bool _isClickable;
    [ObservableProperty] private double _fillOpacity = 1;
    [ObservableProperty] private double _markOpacity = 1;
    [ObservableProperty] private string? _tooltip;

    internal void Show(DateOnly date, StripLevel level, bool isFuture, bool isUntracked, bool isToday, bool isPicked,
        bool isOutsideMonth, string tooltip)
    {
        Date = date;
        IsFuture = isFuture;
        IsUntracked = !isFuture && isUntracked;
        bool shaded = !IsFuture && !IsUntracked;
        IsEmpty = shaded && level.Tone == StripTone.Empty;
        IsNeutral = shaded && level.Tone == StripTone.Neutral;
        IsAccent = shaded && level is { Tone: StripTone.Gain, Step: <= 3 };
        IsBright = shaded && level is { Tone: StripTone.Gain, Step: 4 };
        IsLoss = shaded && level.Tone == StripTone.Loss;
        IsLossBright = shaded && level is { Tone: StripTone.Loss, Step: 4 };
        IsToday = isToday;
        IsPicked = isPicked;
        IsClickable = !isFuture;
        double monthDimming = isOutsideMonth && shaded ? .35 : 1;
        FillOpacity = (shaded ? LevelOpacity[level.Step] : 1) * monthDimming;
        MarkOpacity = monthDimming;
        Tooltip = IsFuture ? null : IsUntracked ? $"{date.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant()} · before EVE Together tracked runs" : tooltip;
    }

    [RelayCommand]
    private void Click() => clicked(this);
}

/// <summary>One week's segment in the bar under the grid: a click picks the week, a second click lets it go.</summary>
public sealed partial class RunsStripWeekViewModel(Action<RunsStripWeekViewModel> clicked) : ObservableObject
{
    public DateOnly WeekStart { get; private set; }

    [ObservableProperty] private bool _isClickable;
    [ObservableProperty] private bool _isPicked;
    [ObservableProperty] private string? _tooltip;

    internal void Show(DateOnly weekStart, bool isClickable, bool isPicked, string tooltip)
    {
        WeekStart = weekStart;
        IsClickable = isClickable;
        IsPicked = isPicked;
        Tooltip = isClickable ? tooltip : null;
    }

    [RelayCommand]
    private void Click() => clicked(this);
}

/// <summary>A column's slot above the grid, holding a month's name where one starts or nothing.</summary>
public sealed partial class RunsStripMonthLabelViewModel(Action<RunsStripMonthLabelViewModel> clicked) : ObservableObject
{
    public DateOnly? Month { get; private set; }

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _hasLabel;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _tooltip;

    internal void Show(DateOnly? month, bool isActive)
    {
        Month = month;
        HasLabel = month is not null;
        IsActive = isActive;
        Text = month?.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant() ?? string.Empty;
        Tooltip = month?.ToString("'Show' MMMM yyyy", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void Click() => clicked(this);
}

public sealed partial class RunsStripWeekdayLabel : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
}
