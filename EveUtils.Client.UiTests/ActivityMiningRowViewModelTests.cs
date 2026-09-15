using System;
using EveUtils.Client.ViewModels.Runs;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-283: the ore-row figures the compact mining ledger reads — units and crit in their own columns (ET-288),
/// and the inline share bar that only draws when a caller actually has something to compare against.</summary>
public sealed class ActivityMiningRowViewModelTests
{
    [Fact]
    public void UnitsText_WithCrit_ReadsTheBareFigure_AndTheCritItsOwn()
    {
        ActivityMiningRowViewModel row = new(Guid.NewGuid(), 1, "Veldspar II-Grade", 61554, 1200, 0, null, false);

        Assert.Equal("61,554", row.UnitsText);
        Assert.Equal("+1,200", row.CritText);
    }

    [Fact]
    public void UnitsText_WithoutCrit_ReadsThePlainFigure_AndADashForCrit()
    {
        ActivityMiningRowViewModel row = new(Guid.NewGuid(), 1, "Veldspar", 5890, 0, 0, null, false);

        Assert.Equal("5,890", row.UnitsText);
        Assert.Equal("—", row.CritText);
    }

    [Fact]
    public void ShareFraction_Given_ExposesAPercentTextAndDrawsABar()
    {
        ActivityMiningRowViewModel row = new(Guid.NewGuid(), 1, "Veldspar II-Grade", 268920, 0, 0, 2726849m, false,
            shareFraction: 0.72);

        Assert.True(row.HasShareBar);
        Assert.Equal("72%", row.ShareText);
    }

    [Fact]
    public void ShareFraction_Omitted_DrawsNoBarAtAll()
    {
        ActivityMiningRowViewModel row = new(Guid.NewGuid(), 1, "Veldspar", 100, 0, 0, null, false);

        Assert.False(row.HasShareBar);
        Assert.Equal(string.Empty, row.ShareText);
    }
}
