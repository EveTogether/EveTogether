using Avalonia.Headless.XUnit;
using EveUtils.Client.Calendar;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-294: SUMMARY for the day, the week and the month — counted from the range line's own filtered facts,
/// in the pane beside the list, never changing the list.</summary>
public sealed class RunsSummaryTests
{
    private const long PilotId = 90000001;
    private static readonly IReadOnlyList<Character> Pilot = [new("Ra Vinter", (int)PilotId)];

    /// <summary>A week picked across a month boundary adds up whole — the day in the month before included — to the very
    /// figures the range line shows for it, BY CHARACTER adds up to the hero figure, DAYS repeats the strip's own
    /// tooltip, and a week-start change re-lays WEEK without closing it. Counter-proof: count the week from the month's
    /// rows only and the activity on the last day of the previous month goes missing (1 activity, +2M); reset the scope
    /// on the week-start change and the summary falls back to MONTH.</summary>
    [AvaloniaFact]
    public async Task WeekSummary_AddsUpTheWholeWeek_LikeTheRangeLine_AndSurvivesAWeekStartChange()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        IWeekStartService weekStart = instance.Services.GetRequiredService<IWeekStartService>();
        var monthFirst = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
        // The first of the month must not open its week, or the day before it would be a week of its own.
        DayOfWeek firstDay = monthFirst.DayOfWeek == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;
        weekStart.Apply(firstDay);
        DateOnly lastOfPrevious = monthFirst.AddDays(-1);
        await _SaveRunAsync(dispatcher, lastOfPrevious.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Local), 1_000_000m, cancellationToken);
        await _SaveRunAsync(dispatcher, monthFirst.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Local), 2_000_000m, cancellationToken);

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);
        viewModel.ApplyWidth(1303);
        DateOnly week = WeekMath.StartOf(monthFirst, firstDay);
        await viewModel.PickWeekAsync(week);
        RunsSummaryViewModel summary = viewModel.Summary;

        Assert.True(viewModel.ShowsSummaryInPane);
        Assert.True(viewModel.IsSummaryOn);
        Assert.Equal(RunsRangeKind.Week, summary.Scope);
        Assert.Equal(RunsSummaryViewModel.WeekTitle(week), summary.TitleText);
        Assert.Equal("2 activities", viewModel.RangeCountText);
        Assert.StartsWith($"{viewModel.RangeCountText} · {viewModel.RangeFlownText}", summary.MetaText);
        Assert.Equal("+3M ISK", summary.NetText);
        Assert.Equal("+3M ISK net", viewModel.RangeNetText);
        RunsSummaryCharacterLine pilot = Assert.Single(summary.ByCharacter);
        Assert.Equal((2, "3M"), (pilot.Runs, pilot.IskText));
        Assert.Equal(["2M", "1M"], summary.TopRuns.Select(line => line.IskText));
        Assert.Equal(viewModel.Strip.Cells.Single(cell => cell.Date == lastOfPrevious).Tooltip,
            summary.WeekDays.Single(cell => cell.Date == lastOfPrevious).Tooltip);

        DayOfWeek otherStart = firstDay == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;
        weekStart.Apply(otherStart);

        Assert.Equal(RunsRangeKind.Week, summary.Scope);
        Assert.Equal(otherStart, summary.WeekDays[0].Date.DayOfWeek);
        Assert.Equal(RunsSummaryViewModel.WeekTitle(summary.WeekDays[0].Date), summary.TitleText);
    }

    /// <summary>Switching DAY | WEEK | MONTH never touches the list; a DAYS cell picks its day as the strip would; a TOP
    /// RUNS line selects that run and shows it in the pane, and SUMMARY swaps back; a TYPES filter reaches the summary
    /// the way it reaches the range line. Counter-proof: count the summary from an unfiltered read and the filtered
    /// summary still says 2 activities; leave IsSummaryChosen set on a TOP RUNS click and the pane stays on the summary.</summary>
    [AvaloniaFact]
    public async Task Summary_ScopesPicksAndOpens_WithoutChangingTheList_AndFollowsTheFilters()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        instance.Services.GetRequiredService<IWeekStartService>().Apply(DayOfWeek.Monday);
        DateTime evening = new(DateTime.Now.Year, DateTime.Now.Month, 1, 20, 0, 0, DateTimeKind.Local);
        await _SaveRunAsync(dispatcher, evening, 5_000_000m, cancellationToken);
        await _SaveRunAsync(dispatcher, evening.AddHours(1), 7_000_000m, cancellationToken);

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);
        viewModel.ApplyWidth(1303);
        RunsSummaryViewModel summary = viewModel.Summary;
        RunsTabViewModel tab = viewModel.Tabs[0];
        int listed = tab.Items.Count;

        Assert.Equal(RunsRangeKind.Month, summary.Scope);
        Assert.Single(summary.TopSites);
        summary.ChooseDayCommand.Execute(null);
        Assert.Equal(RunsRangeKind.Day, summary.Scope);
        Assert.Equal(RunsRangeKind.Month, viewModel.RangeKind);
        Assert.Equal(listed, tab.Items.Count);
        Assert.Equal("first start 20:00 · last end 21:15", summary.HoursSummaryText);
        Assert.False(summary.Hours[19].IsAccent || summary.Hours[19].IsBright);
        Assert.True(summary.Hours[20].IsAccent || summary.Hours[20].IsBright);

        summary.ChooseWeekCommand.Execute(null);
        DateOnly day = DateOnly.FromDateTime(evening);
        summary.WeekDays.Single(cell => cell.Date == day).ClickCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => viewModel.RangeKind == RunsRangeKind.Day);
        Assert.Equal(RunsRangeKind.Day, summary.Scope);
        Assert.Equal(day, viewModel.RangeStart);

        RunsSummaryRunLine top = summary.TopRuns[0];
        Assert.Equal("7M", top.IskText);
        summary.OpenRunCommand.Execute(top);
        await ActivityWindowHarness.WaitUntil(() => viewModel.SelectedRow is not null);
        Assert.Equal(top.ActivitySummaryId, viewModel.SelectedRow!.ActivitySummaryId);
        Assert.False(viewModel.ShowsSummaryInPane);
        Assert.False(viewModel.IsSummaryOn);

        viewModel.ToggleSummary();
        Assert.True(viewModel.ShowsSummaryInPane);
        viewModel.ToggleSummary();
        Assert.False(viewModel.ShowsSummaryInPane);
        viewModel.ToggleSummary();

        RunFilterTileViewModel type = Assert.Single(viewModel.TypeFilter.Tiles);
        type.Toggle();
        Assert.Equal("0 activities", viewModel.RangeCountText);
        Assert.StartsWith(viewModel.RangeCountText, summary.MetaText);
        Assert.False(summary.HasRows);
    }

    private static async Task<RunsOverviewViewModel> _OpenAsync(TestClientInstance instance, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, Pilot,
            runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    private static async Task _SaveRunAsync(ICqrsDispatcher dispatcher, DateTime startedAtLocal, decimal bounty,
        CancellationToken cancellationToken)
    {
        DateTime startedAtUtc = startedAtLocal.ToUniversalTime();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(PilotId, ActivityKind.Site, startedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [], [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = bounty }], [], []),
            cancellationToken);
    }
}
