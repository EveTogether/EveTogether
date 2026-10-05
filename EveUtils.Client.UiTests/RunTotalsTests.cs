using System;
using System.Collections.Generic;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-436: the run totals the home's earnings tiles show and the ones the Local API's <c>runs/summary</c> hands
/// a stream overlay are counted by the same code over the same window, so the two never show different figures.</summary>
public sealed class RunTotalsTests
{
    private const long CharacterId = 90000001;

    /// <summary>Tuesday 22 September 2026, 21:10.</summary>
    private static readonly DateTime Now = new(2026, 9, 22, 21, 10, 0);

    private static readonly DateTime SessionStart = new(2026, 9, 22, 18, 0, 0);

    private static readonly IReadOnlyDictionary<long, string> Characters = new Dictionary<long, string> { [CharacterId] = "Pilot" };

    private static RunsActivityFacts _Activity(DateTime startedAtLocal, decimal? isk, RunTypeId type = RunTypeId.CombatSite,
        int minutes = 30, string? filament = null) =>
        new(startedAtLocal, TimeSpan.FromMinutes(minutes), isk,
            isk is { } amount ? new IskBreakdown([new IskContribution(IskSource.Bounty, amount, IskCertainty.Measured)]) : IskBreakdown.None,
            [], type, [CharacterId], AbyssalFilamentText: filament);

    private static RunsActivityFacts[] _Month() =>
    [
        _Activity(new DateTime(2026, 9, 2, 20, 0, 0), 7_000_000m, minutes: 45),
        _Activity(new DateTime(2026, 9, 20, 20, 0, 0), 5_000_000m),
        _Activity(new DateTime(2026, 9, 21, 20, 0, 0), 2_000_000m, minutes: 20),
        _Activity(new DateTime(2026, 9, 22, 17, 0, 0), null, minutes: 10),
        _Activity(new DateTime(2026, 9, 22, 19, 0, 0), 3_000_000m, minutes: 0),
        _Activity(new DateTime(2026, 9, 22, 20, 0, 0), 1_000_000m, minutes: 15),
        _Activity(new DateTime(2026, 9, 22, 22, 0, 0), 9_000_000m)
    ];

    private static RunsSummaryDto _Summary(IReadOnlyList<RunsActivityFacts> activities, RunsPeriod period, RunsKind kind = RunsKind.All) =>
        LocalApiRuns.Summarise(activities, period, kind, Now, DayOfWeek.Monday, SessionStart, Characters, null);

    /// <summary>The figures a widget gets are the tile's own. Counter-proof: a summary that counted its own window
    /// (a UTC day, the whole week) or added up ISK/hour over the untimed run would differ in one of the three periods.</summary>
    [Theory]
    [InlineData(EarningsPeriodKind.Today, RunsPeriod.Today)]
    [InlineData(EarningsPeriodKind.Week, RunsPeriod.Week)]
    [InlineData(EarningsPeriodKind.Month, RunsPeriod.Month)]
    public void Summary_SamePeriod_ShowsTheHomeTilesFigures(EarningsPeriodKind tileKind, RunsPeriod period)
    {
        RunsActivityFacts[] activities = _Month();

        EarningsPeriodFigures tile = EarningsPeriods.For(tileKind, activities, Now, DayOfWeek.Monday, null);
        RunsSummaryDto api = _Summary(activities, period);

        Assert.Equal(tile.Runs, api.Runs);
        Assert.Equal(tile.Net, api.Isk);
        Assert.Equal(tile.PerHour, api.IskPerHour);
        Assert.Equal((long)tile.Flown.TotalSeconds, api.FlownSeconds);
        Assert.Equal(tile.Start.ToDateTime(TimeOnly.MinValue).ToUniversalTime(), api.FromUtc);
    }

    /// <summary>Today until 21:10: the 22:00 run has not happened yet, the unvalued one adds nothing, and the untimed
    /// one adds ISK but no hours, so it stays out of the rate (4M over 25 minutes would read 9.6M/h).</summary>
    [Fact]
    public void Summary_Today_CountsUntilNowAndRatesOnlyTimedRuns()
    {
        RunsSummaryDto today = _Summary(_Month(), RunsPeriod.Today);

        Assert.Equal(3, today.Runs);
        Assert.Equal(4_000_000m, today.Isk);
        Assert.Equal(1_000_000m / (decimal)(25.0 / 60.0), today.IskPerHour);
        Assert.Equal(25 * 60, today.FlownSeconds);
        Assert.Equal(750, today.AverageRunSeconds);
    }

    /// <summary>A session counts from the moment it started, not from midnight: the 17:00 run is today's but not the
    /// session's.</summary>
    [Fact]
    public void Summary_Session_CountsFromTheSessionStart()
    {
        RunsSummaryDto session = _Summary(_Month(), RunsPeriod.Session);

        Assert.Equal(2, session.Runs);
        Assert.Equal(4_000_000m, session.Isk);
        Assert.Equal(SessionStart.ToUniversalTime(), session.FromUtc);
    }

    [Fact]
    public void Summary_NothingValued_ReadsNoIskRatherThanZero()
    {
        RunsSummaryDto summary = _Summary([_Activity(new DateTime(2026, 9, 22, 19, 0, 0), null)], RunsPeriod.Today);

        Assert.Equal(1, summary.Runs);
        Assert.Null(summary.Isk);
        Assert.Null(summary.IskPerHour);
        Assert.Null(summary.AverageIskPerRun);
    }

    /// <summary>A kind counts only its own runs: a homefront is combat, an ore or gas site is mining, and a data site
    /// only counts under all.</summary>
    [Theory]
    [InlineData(RunsKind.All, 7)]
    [InlineData(RunsKind.Abyssal, 1)]
    [InlineData(RunsKind.Combat, 2)]
    [InlineData(RunsKind.Mission, 1)]
    [InlineData(RunsKind.Mining, 2)]
    public void Summary_Kind_CountsOnlyThatKindsRuns(RunsKind kind, int expectedRuns)
    {
        var at = new DateTime(2026, 9, 22, 19, 0, 0);
        RunsActivityFacts[] activities =
        [
            _Activity(at, 1m, RunTypeId.Abyssal),
            _Activity(at, 1m, RunTypeId.CombatSite),
            _Activity(at, 1m, RunTypeId.Homefront),
            _Activity(at, 1m, RunTypeId.Mission),
            _Activity(at, 1m, RunTypeId.OreSite),
            _Activity(at, 1m, RunTypeId.GasSite),
            _Activity(at, 1m, RunTypeId.DataSite)
        ];

        Assert.Equal(expectedRuns, _Summary(activities, RunsPeriod.Today, kind).Runs);
    }

    /// <summary>Abyssal totals split per tier and weather, lowest tier first, with the runs saved before the tier was
    /// kept in a bucket of their own so the breakdown still adds up to the total.</summary>
    [Fact]
    public void Summary_Abyssal_BreaksDownPerTierAndWeather()
    {
        var at = new DateTime(2026, 9, 22, 19, 0, 0);
        RunsActivityFacts[] activities =
        [
            _Activity(at, 30_000_000m, RunTypeId.Abyssal, 20, "4|Dark"),
            _Activity(at, 50_000_000m, RunTypeId.Abyssal, 15, "4|Dark"),
            _Activity(at, 10_000_000m, RunTypeId.Abyssal, 18, "2|Firestorm"),
            _Activity(at, 5_000_000m, RunTypeId.Abyssal, 19),
            _Activity(at, 99_000_000m, RunTypeId.CombatSite)
        ];

        RunsSummaryDto summary = _Summary(activities, RunsPeriod.Today, RunsKind.Abyssal);

        Assert.Equal(95_000_000m, summary.Isk);
        Assert.NotNull(summary.Abyssal);
        Assert.Collection(summary.Abyssal,
            agitated =>
            {
                Assert.Equal(2, agitated.Tier);
                Assert.Equal("Agitated", agitated.TierName);
                Assert.Equal("Firestorm", agitated.Weather);
                Assert.Equal(1, agitated.Runs);
                Assert.Equal(10_000_000m, agitated.Isk);
            },
            raging =>
            {
                Assert.Equal(4, raging.Tier);
                Assert.Equal("Raging", raging.TierName);
                Assert.Equal("Dark", raging.Weather);
                Assert.Equal(2, raging.Runs);
                Assert.Equal(80_000_000m, raging.Isk);
                Assert.Equal(1050, raging.AverageClearSeconds);
            },
            unknown =>
            {
                Assert.Null(unknown.Tier);
                Assert.Null(unknown.Weather);
                Assert.Equal(1, unknown.Runs);
            });
        Assert.Null(_Summary(activities, RunsPeriod.Today).Abyssal);
    }

    /// <summary>The query string takes a member's name in any case, and nothing else — an enum parse would also take
    /// "1" or "99".</summary>
    [Theory]
    [InlineData(null, true, RunsPeriod.Today)]
    [InlineData("", true, RunsPeriod.Today)]
    [InlineData("session", true, RunsPeriod.Session)]
    [InlineData("WEEK", true, RunsPeriod.Week)]
    [InlineData("1", false, RunsPeriod.Today)]
    [InlineData("99", false, RunsPeriod.Today)]
    [InlineData("year", false, RunsPeriod.Today)]
    public void TryParse_Period_TakesOnlyMemberNames(string? text, bool expectedValid, RunsPeriod expected)
    {
        bool valid = LocalApiRuns.TryParse(text, RunsPeriod.Today, out RunsPeriod period);

        Assert.Equal(expectedValid, valid);
        if (valid)
            Assert.Equal(expected, period);
    }
}
