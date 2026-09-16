using System;
using System.Linq;
using EveUtils.Client.Calendar;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// WeekMath (ET-297): the one week calculation the strip (ET-292) and the WEEK summary (ET-294) will both call.
/// Pins the ticket's worked examples, including the 2026 DST change and a year boundary — never <c>+7×24h</c>,
/// which would be wrong across the former.
/// </summary>
public class WeekMathTests
{
    [Fact]
    public void StartOf_SundayUnderMondayStart_GoesBackToPrecedingMonday()
    {
        var sunday = new DateOnly(2026, 9, 13);
        Assert.Equal(new DateOnly(2026, 9, 7), WeekMath.StartOf(sunday, DayOfWeek.Monday));
    }

    [Fact]
    public void StartOf_SundayUnderSundayStart_IsItself()
    {
        var sunday = new DateOnly(2026, 9, 13);
        Assert.Equal(sunday, WeekMath.StartOf(sunday, DayOfWeek.Sunday));
    }

    [Fact]
    public void StartOf_AcrossDutchDstChange_IsUnaffected()
    {
        // 2026-10-25 is a Sunday and the NL clocks-back date; StartOf must land on DateOnly maths, not +7x24h.
        var dstSunday = new DateOnly(2026, 10, 25);
        Assert.Equal(new DateOnly(2026, 10, 19), WeekMath.StartOf(dstSunday, DayOfWeek.Monday));
    }

    [Fact]
    public void StartOf_MonthBoundary_AndRangeText_SpansTwoMonths()
    {
        var startOfMonth = new DateOnly(2026, 10, 1);
        var weekStart = WeekMath.StartOf(startOfMonth, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 9, 28), weekStart);
        Assert.Equal("28 SEP – 4 OCT", WeekMath.RangeText(weekStart));
    }

    [Fact]
    public void RangeText_SameMonth_UsesDayRangeAndOneMonthLabel()
    {
        Assert.Equal("7–13 SEP", WeekMath.RangeText(new DateOnly(2026, 9, 7)));
    }

    [Fact]
    public void RangeText_YearBoundary_SpansDecemberToJanuary()
    {
        // 2025-12-29 (Monday) .. 2026-01-04 (Sunday).
        Assert.Equal("29 DEC – 4 JAN", WeekMath.RangeText(new DateOnly(2025, 12, 29)));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Sunday)]
    public void DaysInOrder_StartsAtGivenDay_AndHasSevenUniqueDays(DayOfWeek firstDay)
    {
        var days = WeekMath.DaysInOrder(firstDay);

        Assert.Equal(7, days.Count);
        Assert.Equal(firstDay, days[0]);
        Assert.Equal(7, days.Distinct().Count());
    }
}
