using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-385: a day that lost ISK is a shade of its own — not the empty fill of a day with nothing in it.</summary>
public sealed class RunsStripLossTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    [Fact]
    public void LevelScale_LossesStepOnTheirOwnMagnitudes_GainsKeepTheirQuartiles()
    {
        Func<decimal, bool, StripLevel> levelOf = RunsActivityStripViewModel.LevelScale([-2.15e9m, -1e8m, 5e8m, 1e9m]);

        Assert.Equal(new StripLevel(StripTone.Loss, 4), levelOf(-2.15e9m, true));
        Assert.Equal(new StripLevel(StripTone.Loss, 2), levelOf(-1e8m, true));
        Assert.Equal(new StripLevel(StripTone.Gain, 1), levelOf(5e8m, true));
        Assert.Equal(new StripLevel(StripTone.Gain, 2), levelOf(1e9m, true));
    }

    [Fact]
    public void LevelScale_ALoneLoss_IsTheStrongestStep()
    {
        Func<decimal, bool, StripLevel> levelOf = RunsActivityStripViewModel.LevelScale([-5e6m, 0m, 3e9m]);

        Assert.Equal(new StripLevel(StripTone.Loss, 4), levelOf(-5e6m, true));
    }

    [Fact]
    public void LevelScale_ZeroIsEmptyWithoutActivity_AndNeutralWithIt()
    {
        Func<decimal, bool, StripLevel> levelOf = RunsActivityStripViewModel.LevelScale([0m, 1e9m]);

        Assert.Equal(StripLevel.Empty, levelOf(0m, false));
        Assert.Equal(StripLevel.Neutral, levelOf(0m, true));
    }

    [Fact]
    public void Strip_ALossDayIsLossAndNotEmpty_ADayWithNothingIsEmpty_ANullNetDayIsNeutral()
    {
        DateOnly loss = Today.AddDays(-1);
        DateOnly unknown = Today.AddDays(-2);
        DateOnly quiet = Today.AddDays(-3);
        IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> days = new Dictionary<DateOnly, IReadOnlyList<RunsActivityFacts>>
        {
            [loss] = [_Facts(loss, -2.78e9m), _Facts(loss, 0.63e9m)],
            [unknown] = [_Facts(unknown, null)],
            [Today] = [_Facts(Today, 1e9m)]
        };

        RunsActivityStripViewModel strip = _Strip(days, RunsStripShade.Isk);

        RunsStripCellViewModel lossCell = _CellOf(strip, loss);
        Assert.True(lossCell.IsLoss);
        Assert.True(lossCell.IsLossBright);
        Assert.False(lossCell.IsEmpty);
        Assert.False(lossCell.IsAccent || lossCell.IsBright || lossCell.IsNeutral);
        Assert.True(_CellOf(strip, unknown).IsNeutral);
        Assert.False(_CellOf(strip, unknown).IsEmpty);
        Assert.True(_CellOf(strip, quiet).IsEmpty);
        Assert.False(_CellOf(strip, quiet).IsNeutral);
        Assert.True(_CellOf(strip, Today).IsAccent);
    }

    [Fact]
    public void Strip_RunsShadeOnTheSameData_NeverFlagsALoss()
    {
        DateOnly loss = Today.AddDays(-1);
        IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> days = new Dictionary<DateOnly, IReadOnlyList<RunsActivityFacts>>
        {
            [loss] = [_Facts(loss, -2.78e9m), _Facts(loss, 0.63e9m)]
        };

        RunsActivityStripViewModel strip = _Strip(days, RunsStripShade.Runs);

        Assert.DoesNotContain(strip.Cells, cell => cell.IsLoss || cell.IsLossBright || cell.IsNeutral);
        Assert.False(_CellOf(strip, loss).IsEmpty);
    }

    [Fact]
    public void SummaryCell_ALossHour_IsLossAndNotEmpty()
    {
        var cell = new RunsSummaryCellViewModel(null);

        cell.Show(new StripLevel(StripTone.Loss, 2), "tooltip");

        Assert.True(cell.IsLoss);
        Assert.False(cell.IsLossBright);
        Assert.False(cell.IsEmpty || cell.IsAccent || cell.IsBright);
    }

    private static RunsActivityStripViewModel _Strip(
        IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> days, RunsStripShade shade)
    {
        var strip = new RunsActivityStripViewModel(_ => { }, _ => { }, _ => { }) { Shade = shade };
        strip.Show(new RunsStripInput(DayOfWeek.Monday, Today, Today.AddDays(-60), new DateOnly(Today.Year, Today.Month, 1),
            RunsRangeKind.Month, default, days));
        return strip;
    }

    private static RunsStripCellViewModel _CellOf(RunsActivityStripViewModel strip, DateOnly day) =>
        Assert.Single(strip.Cells, cell => cell.Date == day);

    private static RunsActivityFacts _Facts(DateOnly day, decimal? netIsk) =>
        new(day.ToDateTime(new TimeOnly(20, 0)), TimeSpan.FromMinutes(30), netIsk, IskBreakdown.None, [], RunTypeId.Unknown, []);
}
