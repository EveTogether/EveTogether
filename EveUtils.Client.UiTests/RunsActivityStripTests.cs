using Avalonia.Headless.XUnit;
using EveUtils.Client.Calendar;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-292: the activity strip above the runs list — its weeks follow the "Week starts on" setting, live, and a
/// click on a day picks and unfolds that day without filtering the list.</summary>
public sealed class RunsActivityStripTests
{
    private static readonly IReadOnlyList<Character> Pilot = [new("Ra Vinter", 90000001)];

    /// <summary>The grid is laid out by <see cref="WeekMath"/> with the setting's first day on top, and a change to the
    /// setting re-lays the open strip: a picked day survives it, a picked week does not (the same dates are another
    /// week now). Counter-proof: lay the rows out by <c>(int)DayOfWeek</c> — Sunday on top whatever the setting says —
    /// and the Monday half goes red; read the week start once in the constructor and the Sunday half does.</summary>
    [AvaloniaFact]
    public async Task Strip_FollowsTheWeekStart_AndRelaysLiveWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IWeekStartService weekStart = instance.Services.GetRequiredService<IWeekStartService>();
        weekStart.Apply(DayOfWeek.Monday);
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);

        _AssertLaidOutFrom(viewModel.Strip, DayOfWeek.Monday, today);
        Assert.Equal(["", "Tue", "", "Thu", "", "Sat", ""], viewModel.Strip.WeekdayLabels.Select(label => label.Text));

        await viewModel.PickDayAsync(today);
        weekStart.Apply(DayOfWeek.Sunday);

        _AssertLaidOutFrom(viewModel.Strip, DayOfWeek.Sunday, today);
        Assert.Equal(["", "Mon", "", "Wed", "", "Fri", ""], viewModel.Strip.WeekdayLabels.Select(label => label.Text));
        Assert.Equal(RunsRangeKind.Day, viewModel.RangeKind);
        Assert.True(Assert.Single(viewModel.Strip.Cells, cell => cell.Date == today).IsPicked);

        DateOnly sundayWeek = WeekMath.StartOf(today, DayOfWeek.Sunday);
        await viewModel.PickWeekAsync(sundayWeek);
        Assert.Equal(WeekMath.RangeText(sundayWeek), viewModel.RangeTitleText);
        weekStart.Apply(DayOfWeek.Monday);

        _AssertLaidOutFrom(viewModel.Strip, DayOfWeek.Monday, today);
        Assert.Equal(RunsRangeKind.Month, viewModel.RangeKind);
        Assert.DoesNotContain(viewModel.Strip.WeekSegments, segment => segment.IsPicked);
    }

    /// <summary>Besluit Jithran, 15 September: a click on a day in the strip unfolds that day, asks for it at the top of
    /// the list and puts its totals on the range line — and filters nothing. Every day of the month stays listed, a
    /// day he folded stays folded, a day he unfolded stays unfolded, and a second click goes back to the month with the
    /// picked day still open. Counter-proof: show only the picked day's rows (the v1 filter) and the list loses the
    /// other two days; unfold through <c>RunsDayViewModel.Toggle</c> and the second click folds the day again.</summary>
    [AvaloniaFact]
    public async Task StripClick_UnfoldsAndPicksTheDay_WithoutFilteringTheList()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        DateTime monthStart = new(DateTime.Now.Year, DateTime.Now.Month, 1, 20, 0, 0, DateTimeKind.Local);
        await _SaveRunAsync(dispatcher, monthStart, cancellationToken);
        await _SaveRunAsync(dispatcher, monthStart.AddDays(1), cancellationToken);
        await _SaveRunAsync(dispatcher, monthStart.AddDays(1).AddHours(1), cancellationToken);
        await _SaveRunAsync(dispatcher, monthStart.AddDays(2), cancellationToken);

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);
        RunsTabViewModel tab = viewModel.Tabs[0];
        RunsDayViewModel first = tab.Days.Single(day => day.Day == monthStart.Date);
        RunsDayViewModel picked = tab.Days.Single(day => day.Day == monthStart.Date.AddDays(1));
        RunsDayViewModel latest = tab.Days.Single(day => day.Day == monthStart.Date.AddDays(2));
        Assert.True(latest.IsExpanded);   // the newest day opens by default (ET-199)
        Assert.False(picked.IsExpanded);
        List<RunsDayViewModel> scrolledTo = [];
        viewModel.DayScrollRequested += scrolledTo.Add;

        DateOnly pickedDate = DateOnly.FromDateTime(picked.Day);
        viewModel.Strip.Cells.Single(cell => cell.Date == pickedDate).ClickCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => viewModel.RangeKind == RunsRangeKind.Day);

        Assert.True(picked.IsExpanded);
        Assert.Equal([picked], scrolledTo);
        Assert.False(first.IsExpanded);
        Assert.True(latest.IsExpanded);
        Assert.Equal(3, tab.Days.Count);
        Assert.Equal(4, tab.Days.Sum(day => day.Rows.Count));
        Assert.All(tab.Days, day => Assert.Contains(day, tab.Items));
        Assert.Equal(picked.DayText, viewModel.RangeTitleText);
        Assert.Equal(picked.CountText, viewModel.RangeCountText);
        Assert.Equal(picked.FlownText, viewModel.RangeFlownText);
        Assert.Equal(picked.NetText, viewModel.RangeNetText);
        Assert.True(viewModel.Strip.Cells.Single(cell => cell.Date == pickedDate).IsPicked);

        viewModel.Strip.Cells.Single(cell => cell.Date == pickedDate).ClickCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => viewModel.RangeKind == RunsRangeKind.Month);

        Assert.Equal(viewModel.MonthHeaderText, viewModel.RangeTitleText);
        Assert.Equal("4 activities", viewModel.RangeCountText);
        Assert.True(picked.IsExpanded);
        Assert.DoesNotContain(viewModel.Strip.Cells, cell => cell.IsPicked);
    }

    /// <summary>Every row of the grid is one weekday, in the setting's order, and today sits in the last column.</summary>
    private static void _AssertLaidOutFrom(RunsActivityStripViewModel strip, DayOfWeek firstDay, DateOnly today)
    {
        IReadOnlyList<DayOfWeek> order = WeekMath.DaysInOrder(firstDay);
        for (int row = 0; row < 7; row++)
        for (int week = 0; week < RunsActivityStripViewModel.Weeks; week++)
            Assert.Equal(order[row], strip.Cells[row * RunsActivityStripViewModel.Weeks + week].Date.DayOfWeek);

        Assert.Equal(WeekMath.StartOf(today, firstDay).AddDays(-7 * (RunsActivityStripViewModel.Weeks - 1)), strip.Start);
        Assert.Contains(Enumerable.Range(0, 7).Select(row => strip.Cells[row * RunsActivityStripViewModel.Weeks + 11]),
            cell => cell.Date == today && cell.IsToday);
    }

    private static async Task<RunsOverviewViewModel> _OpenAsync(TestClientInstance instance, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, Pilot,
            runClock: false);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    private static async Task _SaveRunAsync(ICqrsDispatcher dispatcher, DateTime startedAtLocal, CancellationToken cancellationToken)
    {
        DateTime startedAtUtc = startedAtLocal.ToUniversalTime();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, startedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15),
            startedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }
}
