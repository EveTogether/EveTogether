using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Calendar;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Everything one drawing of the summary depends on — all of it already on the runs screen, none of it a read.</summary>
/// <param name="Days">The selected tab's activities per local day with the TYPES/CHARACTERS filter applied: the very
/// dictionary the range line and the strip total from, so the three can never disagree (ET-293).</param>
/// <param name="SelectedDay">The selected run's own day, whether or not the pane is showing it.</param>
/// <param name="Server">The one coupled server; null when there is none or several.</param>
public sealed record RunsSummaryInput(
    IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> Days,
    DateOnly Today,
    DateOnly MonthInView,
    RunsRangeKind RangeKind,
    DateOnly RangeStart,
    DateOnly? SelectedDay,
    DayOfWeek FirstDay,
    RunsStripShade Shade,
    (string Address, string Name)? Server,
    IReadOnlyDictionary<long, string> OwnCharacters);

public sealed record RunsSummaryTypeLine(MaterialIconKind Icon, string Name, int Runs, string FlownText, string IskText);

public sealed record RunsSummaryCharacterLine(CharacterFaceViewModel Face, string Name, int Runs, string IskText);

public sealed record RunsSummarySiteLine(string Site, int Runs, string IskText);

public sealed record RunsSummaryRunLine(
    Guid ActivitySummaryId, DateOnly Day, MaterialIconKind Icon, string Site, string CrewText, string StartText,
    string FlownText, string IskText)
{
    public bool HasCrew => CrewText.Length > 0;
}

/// <summary>An hour of the day's HOURS or a day of the week's DAYS: shaded in the strip's own steps (ET-292).</summary>
public sealed partial class RunsSummaryCellViewModel(Action<RunsSummaryCellViewModel>? clicked) : ObservableObject
{
    public DateOnly Date { get; private set; }

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isAccent;
    [ObservableProperty] private bool _isBright;
    [ObservableProperty] private bool _isClickable;
    [ObservableProperty] private double _fillOpacity = 1;
    [ObservableProperty] private string? _tooltip;

    internal void Show(int level, string tooltip, string label = "", DateOnly date = default, bool isClickable = false)
    {
        Date = date;
        Label = label;
        IsEmpty = level == 0;
        IsAccent = level is >= 1 and <= 3;
        IsBright = level == 4;
        FillOpacity = RunsStripCellViewModel.LevelOpacity[level];
        Tooltip = tooltip;
        IsClickable = isClickable && clicked is not null;
    }

    [RelayCommand]
    private void Click()
    {
        if (IsClickable)
            clicked?.Invoke(this);
    }
}

/// <summary>
/// SUMMARY (ET-294): the day, the week or the month on the runs screen, added up — in the pane beside the list at
/// 1303 and in the drawer at 758, the very hosts the selected run uses (ET-291).
///
/// <para><b>Nothing here reads.</b> Every figure is counted from <see cref="RunsSummaryInput.Days"/>, the tab's own
/// filtered facts over the strip and the month in view, through <see cref="RunsActivitySummaryText"/> — so the
/// summary of a scope and the range line on the same scope are one sum, and a week reaching into last month is whole.
/// The own share (ET-296) is already on every fact: nothing is split again here.</para>
///
/// <para><b>Only drawn while it is on screen,</b> and not again for a live refresh that left the activities in scope
/// as they were (ET-287): a new input is compared to the last one drawn before anything is counted.</para>
/// </summary>
public sealed partial class RunsSummaryViewModel : ObservableObject
{
    public const int TopCount = 6;

    private readonly Func<long, string, CharacterFaceViewModel> _faceOf;
    private readonly Action<DateOnly> _dayPicked;
    private readonly Action<RunsSummaryRunLine> _runOpened;

    private RunsSummaryInput? _input;
    private RunsRangeKind? _chosenScope;
    private bool _isActive;
    private DrawnKey? _drawn;

    /// <param name="faceOf">The runs screen's one face per character.</param>
    /// <param name="dayPicked">A DAYS cell: the day picked as a stripklik picks it.</param>
    /// <param name="runOpened">A TOP RUNS line: that run selected and shown.</param>
    public RunsSummaryViewModel(Func<long, string, CharacterFaceViewModel> faceOf, Action<DateOnly> dayPicked,
        Action<RunsSummaryRunLine> runOpened)
    {
        _faceOf = faceOf;
        _dayPicked = dayPicked;
        _runOpened = runOpened;
        Hours = [.. Enumerable.Range(0, 24).Select(_ => new RunsSummaryCellViewModel(null))];
        WeekDays = [.. Enumerable.Range(0, 7).Select(_ => new RunsSummaryCellViewModel(cell => _dayPicked(cell.Date)))];
    }

    // ── Scope ────────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayScope))]
    [NotifyPropertyChangedFor(nameof(IsWeekScope))]
    [NotifyPropertyChangedFor(nameof(IsMonthScope))]
    private RunsRangeKind _scope = RunsRangeKind.Month;

    public bool IsDayScope => Scope == RunsRangeKind.Day;

    public bool IsWeekScope => Scope == RunsRangeKind.Week;

    public bool IsMonthScope => Scope == RunsRangeKind.Month;

    [ObservableProperty] private bool _canChooseDay;
    [ObservableProperty] private bool _canChooseWeek;
    [ObservableProperty] private string _dayTooltip = string.Empty;
    [ObservableProperty] private string _weekTooltip = string.Empty;
    [ObservableProperty] private string _monthTooltip = string.Empty;

    // ── Head ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _kickerText = "THIS MONTH";
    [ObservableProperty] private string _titleText = string.Empty;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private bool _hasNet;
    [ObservableProperty] private string _netText = string.Empty;
    [ObservableProperty] private string _perHourText = string.Empty;
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string _emptyText = string.Empty;

    // ── Sections ─────────────────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private IskBreakdown _isk = IskBreakdown.None;

    public ObservableCollection<RunsIskPartViewModel> IskParts { get; } = [];

    public ObservableCollection<RunsSummaryTypeLine> ByType { get; } = [];

    public ObservableCollection<RunsSummaryCharacterLine> ByCharacter { get; } = [];

    [ObservableProperty] private bool _hasCharacters;

    public ObservableCollection<RunsSummarySiteLine> TopSites { get; } = [];

    public ObservableCollection<RunsSummaryRunLine> TopRuns { get; } = [];

    /// <summary>24 cells, one per local start hour — made once and told what they say.</summary>
    public IReadOnlyList<RunsSummaryCellViewModel> Hours { get; }

    [ObservableProperty] private string _hoursSummaryText = string.Empty;

    /// <summary>7 cells in the week's own order (ET-297).</summary>
    public IReadOnlyList<RunsSummaryCellViewModel> WeekDays { get; }

    [ObservableProperty] private string _shadeHint = string.Empty;

    // ── Driving it ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the summary is on screen. Off, a new input is only kept; on, the last one kept is drawn.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;

            _isActive = value;
            _Draw();
        }
    }

    public void Show(RunsSummaryInput input)
    {
        _input = input;
        _Draw();
    }

    /// <summary>The view changed — another day or week picked, ◀▶, ✕, another month: the scope goes back to what
    /// the view is. A selection or a filter is not a view change and keeps the reader's own choice.</summary>
    public void ResetScope() => _chosenScope = null;

    /// <summary>Kept across a week-start change (ET-297), which lets a picked week go but should not close WEEK.</summary>
    internal RunsRangeKind? ChosenScope
    {
        get => _chosenScope;
        set => _chosenScope = value;
    }

    [RelayCommand]
    private void ChooseDay() => _Choose(RunsRangeKind.Day);

    [RelayCommand]
    private void ChooseWeek() => _Choose(RunsRangeKind.Week);

    [RelayCommand]
    private void ChooseMonth() => _Choose(RunsRangeKind.Month);

    [RelayCommand]
    private void OpenRun(RunsSummaryRunLine? line)
    {
        if (line is not null)
            _runOpened(line);
    }

    private void _Choose(RunsRangeKind scope)
    {
        _chosenScope = scope;
        _Draw();
    }

    private void _Draw()
    {
        if (!_isActive || _input is not { } input)
            return;

        DateOnly monthInView = input.MonthInView;
        DateOnly monthEnd = monthInView.AddMonths(1);
        DateOnly[] daysInMonth = [.. input.Days.Keys.Where(day => day >= monthInView && day < monthEnd)];
        DateOnly? newestInView = input.RangeKind switch
        {
            RunsRangeKind.Day => input.RangeStart,
            RunsRangeKind.Week => _Newest(input.Days.Keys.Where(day => day >= input.RangeStart && day < input.RangeStart.AddDays(7)))
                                  ?? _Newest(daysInMonth),
            _ => _Newest(daysInMonth)
        };

        // The selected run's day and week first — unless a day or week picked since lies elsewhere: a DAYS cell or a
        // strip click has to show the day it was clicked on, whatever row is still selected.
        DateOnly? selected = input.SelectedDay;
        bool selectedInPick = selected is { } inPick && input.RangeKind switch
        {
            RunsRangeKind.Day => inPick == input.RangeStart,
            RunsRangeKind.Week => inPick >= input.RangeStart && inPick < input.RangeStart.AddDays(7),
            _ => true
        };
        (DateOnly? day, string dayWhy) = selectedInPick
            ? (selected, "the selected run's day")
            : input.RangeKind == RunsRangeKind.Day
                ? ((DateOnly?)input.RangeStart, "the day picked in the strip")
                : (newestInView, "the newest day in view");
        (DateOnly? week, string weekWhy) = input.RangeKind switch
        {
            RunsRangeKind.Week => ((DateOnly?)input.RangeStart, selectedInPick ? "the selected run's week" : "the week picked in the strip"),
            RunsRangeKind.Day => ((DateOnly?)WeekMath.StartOf(input.RangeStart, input.FirstDay),
                selectedInPick ? "the selected run's week" : "the week of the day picked in the strip"),
            _ when selected is { } selectedDay => ((DateOnly?)WeekMath.StartOf(selectedDay, input.FirstDay), "the selected run's week"),
            _ => (newestInView is { } newest ? WeekMath.StartOf(newest, input.FirstDay) : null, "the newest week in view")
        };

        RunsRangeKind scope = _chosenScope ?? input.RangeKind;
        if ((scope == RunsRangeKind.Day && day is null) || (scope == RunsRangeKind.Week && week is null))
            scope = RunsRangeKind.Month;

        List<RunsActivityFacts> rows = scope switch
        {
            RunsRangeKind.Day => [.. input.Days.GetValueOrDefault(day!.Value) ?? []],
            RunsRangeKind.Week => [.. Enumerable.Range(0, 7).SelectMany(offset => input.Days.GetValueOrDefault(week!.Value.AddDays(offset)) ?? [])],
            _ => [.. daysInMonth.Order().SelectMany(inMonth => input.Days[inMonth])]
        };
        rows.Sort((first, second) => first.StartedAtLocal.CompareTo(second.StartedAtLocal));

        var key = new DrawnKey(scope, day, week, monthInView, input.Today, input.FirstDay, input.Shade, input.Server,
            dayWhy, weekWhy, rows);
        if (_drawn is { } drawn && drawn.Matches(key))
            return;

        _drawn = key;
        Scope = scope;
        CanChooseDay = day is not null;
        CanChooseWeek = week is not null;
        const string sameFilters = " · same filters as the list";
        DayTooltip = day is { } dayShown ? $"{_Short(dayShown)} — {dayWhy}{sameFilters}" : "No day in view";
        WeekTooltip = week is { } weekShown ? $"{WeekMath.RangeText(weekShown)} — {weekWhy}{sameFilters}" : "No week in view";
        MonthTooltip = $"All of {monthInView.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}{sameFilters}";

        KickerText = scope switch { RunsRangeKind.Day => "THIS DAY", RunsRangeKind.Week => "THIS WEEK", _ => "THIS MONTH" };
        TitleText = scope switch
        {
            RunsRangeKind.Day => day!.Value.ToString("dddd d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
            RunsRangeKind.Week => WeekTitle(week!.Value),
            _ => monthInView.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()
        };

        _ShowHead(rows, scope, input);
        _ShowSources(rows);
        _ShowTypes(rows);
        _ShowCharacters(rows, input.OwnCharacters);
        TopSites.ReconcileTo(scope == RunsRangeKind.Month ? _Reuse(TopSites, _TopSites(rows)) : []);
        TopRuns.ReconcileTo(scope == RunsRangeKind.Month ? [] : _Reuse(TopRuns, _TopRuns(rows, scope)));
        if (scope == RunsRangeKind.Day)
            _ShowHours(rows, input.Shade);
        if (scope == RunsRangeKind.Week)
            _ShowWeekDays(week!.Value, input);
        ShadeHint = input.Shade == RunsStripShade.Isk ? "ISK" : "runs";
    }

    /// <summary>"7–13 SEPTEMBER" inside one month, "28 SEP – 4 OCT" across two.</summary>
    public static string WeekTitle(DateOnly weekStart)
    {
        DateOnly weekEnd = weekStart.AddDays(6);
        return weekStart.Month == weekEnd.Month
            ? $"{weekStart.Day}–{weekEnd.Day} {weekEnd.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant()}"
            : WeekMath.RangeText(weekStart);
    }

    private void _ShowHead(IReadOnlyList<RunsActivityFacts> rows, RunsRangeKind scope, RunsSummaryInput input)
    {
        HasRows = rows.Count > 0;
        EmptyText = $"Nothing in {TitleText} matches these filters.";
        List<string> meta = [RunsActivitySummaryText.ActivitiesCount(rows.Count)];
        if (rows.Count > 0)
        {
            meta.Add(RunsActivitySummaryText.FlownFor(rows));
            if (scope == RunsRangeKind.Day)
                meta.Add($"{_Clock(rows.Min(row => row.StartedAtLocal))}–{_Clock(rows.Max(row => row.StartedAtLocal + row.Duration))}");
            // Named only where there is exactly one server it could be on; anything else is "not published".
            int notOnServer = input.Server is { } server
                ? rows.Count(row => !row.ServerAddresses.Contains(server.Address))
                : rows.Count(row => row.ServerAddresses.Count == 0);
            if (notOnServer > 0)
                meta.Add(input.Server is { } named ? $"{notOnServer} not on {named.Name}" : $"{notOnServer} not published");
        }

        MetaText = string.Join(" · ", meta);

        decimal? net = RunsActivitySummaryText.Net(rows);
        HasNet = net.HasValue;
        NetText = net is { } value ? RunsActivitySummaryText.Signed(value) + " ISK" : "nothing recorded to value";

        PerHourText = RunsActivitySummaryText.PerHour(rows) is { } perHour
            ? IskFormat.Compact(perHour) + " ISK/h"
            : string.Empty;
    }

    private void _ShowSources(IReadOnlyList<RunsActivityFacts> rows)
    {
        Isk = RunsActivitySummaryText.SourcesFor(rows);
        List<RunsIskPartViewModel> parts = [.. RunsActivityPaneViewModel.Sources
            .Select(source => (source.Label, Part: Isk.Of(source.Source)))
            .Where(line => line.Part is { Certainty: not IskCertainty.Unknown, Amount: not 0 })
            .Select(line => new RunsIskPartViewModel(line.Label, new IskBreakdown([line.Part!]), IskFormat.Compact(line.Part!.Amount)))];
        // Spent, not earned: its own line with its minus, never a part of the bar (ET-256).
        if (Isk.Of(IskSource.Consumables) is { Certainty: not IskCertainty.Unknown, Amount: not 0 } consumables)
            parts.Add(new RunsIskPartViewModel("CONSUMABLES", null, IskFormat.Compact(consumables.Amount)));
        IskParts.ReconcileTo(_Reuse(IskParts, parts));
    }

    private void _ShowTypes(IReadOnlyList<RunsActivityFacts> rows)
    {
        List<RunsSummaryTypeLine> lines = [.. rows
            .GroupBy(row => row.TypeId)
            .Select(group => (Definition: RunTypeCatalogue.For(group.Key), Rows: group.ToArray(),
                Net: RunsActivitySummaryText.Net(group)))
            .OrderByDescending(type => type.Net ?? decimal.MinValue)
            .ThenByDescending(type => type.Rows.Length)
            .Select(type => new RunsSummaryTypeLine(type.Definition.Icon, type.Definition.Name, type.Rows.Length,
                RunsActivitySummaryText.Flown(type.Rows), _IskText(type.Net)))];
        ByType.ReconcileTo(_Reuse(ByType, lines));
    }

    /// <summary>This machine's own characters, the ones CHARACTERS offers (ET-293), by runs. Their ISK is each one's
    /// own share off the stored split, so the column adds up to the hero figure (ET-296) — an activity summarised
    /// before that split was stored has none, and counts only where one own character flew it.</summary>
    private void _ShowCharacters(IReadOnlyList<RunsActivityFacts> rows, IReadOnlyDictionary<long, string> ownCharacters)
    {
        List<RunsSummaryCharacterLine> lines = [];
        foreach ((long characterId, string name) in ownCharacters)
        {
            RunsActivityFacts[] flown = [.. rows.Where(row => row.CrewCharacterIds.Contains(characterId))];
            if (flown.Length == 0)
                continue;

            decimal[] shares = [.. flown.Select(row => row.ShareOf(characterId, ownCharacters.ContainsKey)).OfType<decimal>()];
            lines.Add(new RunsSummaryCharacterLine(_faceOf(characterId, name), name, flown.Length,
                _IskText(shares.Length == 0 ? null : shares.Sum())));
        }

        lines.Sort((first, second) => second.Runs != first.Runs
            ? second.Runs.CompareTo(first.Runs)
            : StringComparer.OrdinalIgnoreCase.Compare(first.Name, second.Name));
        ByCharacter.ReconcileTo(_Reuse(ByCharacter, lines));
        HasCharacters = lines.Count > 0;
    }

    private static List<RunsSummarySiteLine> _TopSites(IReadOnlyList<RunsActivityFacts> rows) =>
        [.. rows
            .GroupBy(row => row.SiteText)
            .Select(site => (Site: site.Key, Runs: site.Count(), Net: RunsActivitySummaryText.Net(site)))
            .Where(site => site.Net.HasValue)
            .OrderByDescending(site => site.Net)
            .Take(TopCount)
            .Select(site => new RunsSummarySiteLine(site.Site, site.Runs, _IskText(site.Net)))];

    private static List<RunsSummaryRunLine> _TopRuns(IReadOnlyList<RunsActivityFacts> rows, RunsRangeKind scope) =>
        [.. rows
            .Where(row => row.NetIsk.HasValue)
            .OrderByDescending(row => row.NetIsk)
            .ThenBy(row => row.StartedAtLocal)
            .Take(TopCount)
            .Select(row => new RunsSummaryRunLine(row.ActivitySummaryId, row.Day, RunTypeCatalogue.For(row.TypeId).Icon,
                row.SiteText, row.CrewCount > 1 ? $"×{row.CrewCount}" : string.Empty,
                scope == RunsRangeKind.Day
                    ? row.StartedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : row.StartedAtLocal.ToString("ddd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant(),
                row.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), _IskText(row.NetIsk)))];

    /// <summary>Per local start hour: a run over midnight counts in the hour it started, on the day it started.</summary>
    private void _ShowHours(IReadOnlyList<RunsActivityFacts> rows, RunsStripShade shade)
    {
        RunsActivityFacts[][] byHour = [.. Enumerable.Range(0, 24)
            .Select(hour => rows.Where(row => row.StartedAtLocal.Hour == hour).ToArray())];
        Func<decimal, int> levelOf = RunsActivityStripViewModel.LevelScale(
            byHour.Select(hour => RunsActivityStripViewModel.ValueOf(shade, hour)));
        for (int hour = 0; hour < 24; hour++)
        {
            RunsActivityFacts[] started = byHour[hour];
            string when = $"{hour:00}:00–{(hour + 1) % 24:00}:00";
            string tooltip = started.Length == 0
                ? $"{when} · no runs started"
                : $"{when} · {RunsActivitySummaryText.ActivitiesCount(started.Length)} started"
                  + (RunsActivitySummaryText.Net(started) is { } net ? $" · {RunsActivitySummaryText.Signed(net)} ISK" : string.Empty);
            Hours[hour].Show(levelOf(RunsActivityStripViewModel.ValueOf(shade, started)), tooltip);
        }

        HoursSummaryText = rows.Count == 0
            ? string.Empty
            : $"first start {_Clock(rows.Min(row => row.StartedAtLocal))} · last end {_Clock(rows.Max(row => row.StartedAtLocal + row.Duration))}";
    }

    private void _ShowWeekDays(DateOnly weekStart, RunsSummaryInput input)
    {
        DateOnly[] days = [.. Enumerable.Range(0, 7).Select(weekStart.AddDays)];
        Func<decimal, int> levelOf = RunsActivityStripViewModel.LevelScale(days
            .Select(day => input.Days.TryGetValue(day, out IReadOnlyList<RunsActivityFacts>? facts)
                ? RunsActivityStripViewModel.ValueOf(input.Shade, facts)
                : 0));
        for (int index = 0; index < 7; index++)
        {
            DateOnly day = days[index];
            input.Days.TryGetValue(day, out IReadOnlyList<RunsActivityFacts>? facts);
            WeekDays[index].Show(
                facts is null ? 0 : levelOf(RunsActivityStripViewModel.ValueOf(input.Shade, facts)),
                RunsActivityStripViewModel.DayTooltip(day, facts),
                day.ToString("ddd", CultureInfo.InvariantCulture), day, isClickable: day <= input.Today);
        }
    }

    /// <summary>An equal line already on screen stays the same instance, so a live refresh that moved nothing in a
    /// section moves no control in it.</summary>
    private static List<T> _Reuse<T>(IEnumerable<T> shown, IEnumerable<T> built) where T : class
    {
        List<T> previous = [.. shown];
        return [.. built.Select(line => previous.FirstOrDefault(line.Equals) ?? line)];
    }

    private static DateOnly? _Newest(IEnumerable<DateOnly> days) => days.Cast<DateOnly?>().Max();

    private static string _Short(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>HH:mm, modulo 24 — a last end after midnight reads as the clock does, not as 25:10.</summary>
    private static string _Clock(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string _IskText(decimal? net) => net is { } value ? IskFormat.Compact(value) : "—";

    /// <summary>What the last drawing was made of. Facts carry lists, which a record compares by reference, and a
    /// read hands back new ones every time — so the activities are compared on what the summary actually reads.</summary>
    private sealed record DrawnKey(
        RunsRangeKind Scope, DateOnly? Day, DateOnly? Week, DateOnly Month, DateOnly Today, DayOfWeek FirstDay,
        RunsStripShade Shade, (string Address, string Name)? Server, string DayWhy, string WeekWhy, IReadOnlyList<RunsActivityFacts> Rows)
    {
        public bool Matches(DrawnKey other) =>
            Scope == other.Scope && Day == other.Day && Week == other.Week && Month == other.Month && Today == other.Today
            && FirstDay == other.FirstDay && Shade == other.Shade && Server == other.Server
            && DayWhy == other.DayWhy && WeekWhy == other.WeekWhy
            && Rows.Count == other.Rows.Count && Rows.Zip(other.Rows).All(pair => _Same(pair.First, pair.Second));

        private static bool _Same(RunsActivityFacts first, RunsActivityFacts second) =>
            first.ActivitySummaryId == second.ActivitySummaryId && first.StartedAtLocal == second.StartedAtLocal
            && first.Duration == second.Duration && first.NetIsk == second.NetIsk && first.Isk.Equals(second.Isk)
            && first.TypeId == second.TypeId && first.SiteText == second.SiteText && first.CrewCount == second.CrewCount
            && first.ServerAddresses.SequenceEqual(second.ServerAddresses)
            && first.CrewCharacterIds.SequenceEqual(second.CrewCharacterIds)
            && _SameSplit(first.IskByOwnCharacter, second.IskByOwnCharacter);

        private static bool _SameSplit(IReadOnlyDictionary<long, IskBreakdown>? first, IReadOnlyDictionary<long, IskBreakdown>? second) =>
            first is null || second is null
                ? first is null && second is null
                : first.Count == second.Count
                  && first.All(pair => second.TryGetValue(pair.Key, out IskBreakdown? other) && pair.Value.Equals(other));
    }
}
