using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-485: <c>runs/days</c> counts through the same <see cref="RunTotals"/> as <c>runs/summary</c>, only cut into
/// days whose boundary the caller picks.</summary>
public sealed class RunsDaysTests
{
    private const long CharacterId = 90000001;

    private static readonly DateTime Now = new(2026, 9, 22, 21, 10, 0);

    private static readonly DateTime SessionStart = new(2026, 9, 22, 18, 0, 0);

    private static readonly IReadOnlyDictionary<long, string> Characters = new Dictionary<long, string> { [CharacterId] = "Pilot" };

    private static RunsActivityFacts _Activity(DateTime startedAtLocal, decimal? isk, RunTypeId type = RunTypeId.CombatSite,
        int minutes = 30, IskSource source = IskSource.Bounty) =>
        new(startedAtLocal, TimeSpan.FromMinutes(minutes), isk,
            isk is { } amount ? new IskBreakdown([new IskContribution(source, amount, IskCertainty.Measured)]) : IskBreakdown.None,
            [], type, [CharacterId]);

    private static RunsDaysDto _Days(IReadOnlyCollection<RunsActivityFacts> activities, DateOnly from, DateOnly to,
        string dayStart, bool byKind = false) =>
        LocalApiRuns.Days(activities, from, to, TimeOnly.ParseExact(dayStart, "HH:mm"), byKind, Now, TimeZoneInfo.Utc);

    private static RunsActivityFacts[] _AroundMidnight() =>
    [
        _Activity(new DateTime(2026, 9, 20, 23, 30, 0), 4_000_000m),
        _Activity(new DateTime(2026, 9, 21, 1, 30, 0), 6_000_000m)
    ];

    [Fact]
    public void Days_DayStartSixAm_PutsARunBeforeAndAfterMidnightOnOneDay()
    {
        RunsDaysDto days = _Days(_AroundMidnight(), new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 21), "06:00");

        RunsDayDto evening = Assert.Single(days.Days, day => day.Runs > 0);
        Assert.Equal(new DateOnly(2026, 9, 20), evening.Date);
        Assert.Equal(2, evening.Runs);
        Assert.Equal(10_000_000m, evening.Isk);
        Assert.Equal(0, days.Days.Single(day => day.Date == new DateOnly(2026, 9, 21)).Runs);
    }

    [Fact]
    public void Days_DayStartMidnight_SplitsThoseRunsOverTwoDays()
    {
        RunsDaysDto days = _Days(_AroundMidnight(), new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 21), "00:00");

        Assert.Equal([1, 1], days.Days.Select(day => day.Runs));
        Assert.Equal([4_000_000m, 6_000_000m], days.Days.Select(day => day.Isk));
    }

    [Fact]
    public void Days_OverAMonth_AddUpToTheMonthSummary()
    {
        RunsActivityFacts[] activities =
        [
            _Activity(new DateTime(2026, 9, 2, 20, 0, 0), 7_000_000m, minutes: 45),
            _Activity(new DateTime(2026, 9, 20, 20, 0, 0), 5_000_000m, RunTypeId.Mission),
            _Activity(new DateTime(2026, 9, 21, 20, 0, 0), 2_000_000m, minutes: 20),
            _Activity(new DateTime(2026, 9, 22, 17, 0, 0), null, minutes: 10),
            _Activity(new DateTime(2026, 9, 22, 19, 0, 0), 3_000_000m, minutes: 0),
            _Activity(new DateTime(2026, 9, 22, 22, 0, 0), 9_000_000m),
            _Activity(new DateTime(2026, 8, 31, 23, 0, 0), 8_000_000m)
        ];

        RunsDaysDto days = _Days(activities, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "00:00");
        RunsSummaryDto month = LocalApiRuns.Summarise(activities, RunsPeriod.Month, RunsKind.All, Now, DayOfWeek.Monday,
            SessionStart, Characters, null);

        Assert.Equal(30, days.Days.Count);
        Assert.Equal(month.Runs, days.Days.Sum(day => day.Runs));
        Assert.Equal(month.Isk, days.Days.Sum(day => day.Isk ?? 0m));
        Assert.Equal(month.FlownSeconds, days.Days.Sum(day => day.FlownSeconds));
    }

    [Fact]
    public void Days_SourcesAndKinds_AreSplitPerDay()
    {
        var evening = new DateTime(2026, 9, 21, 20, 0, 0);
        RunsActivityFacts[] activities =
        [
            _Activity(evening, 3_000_000m, RunTypeId.Abyssal, source: IskSource.Loot),
            _Activity(evening, 2_000_000m, RunTypeId.Mission)
        ];

        RunsDayDto day = _Days(activities, new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21), "00:00", byKind: true).Days.Single();

        Assert.Equal(5_000_000m, day.Isk);
        Assert.Equal(3_000_000m, day.Sources.Single(source => source.Source == "loot").Isk);
        Assert.Equal(2_000_000m, day.Sources.Single(source => source.Source == "bounty").Isk);
        Assert.Equal(["abyssal", "mission"], day.Kinds?.Select(kind => kind.Kind));
        Assert.Null(_Days(activities, new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21), "00:00").Days.Single().Kinds);
    }

    [Fact]
    public void Days_NothingValued_HasNoIskInsteadOfZero()
    {
        RunsDayDto day = _Days([_Activity(new DateTime(2026, 9, 21, 20, 0, 0), null)], new DateOnly(2026, 9, 21),
            new DateOnly(2026, 9, 21), "00:00").Days.Single();

        Assert.Equal(1, day.Runs);
        Assert.Null(day.Isk);
        Assert.Null(day.IskPerHour);
    }

    [Fact]
    public void Days_Response_NamesTheZoneAndNoLocation()
    {
        RunsDaysDto days = _Days([], new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21), "06:00");

        Assert.Equal("UTC", days.TimeZone);
        Assert.Equal("+00:00", days.UtcOffset);
        Assert.Equal("06:00", days.DayStart);
    }

    [Theory]
    [InlineData(null, null, null, "from is required")]
    [InlineData("21-09-2026", null, null, "from is required")]
    [InlineData("2026-09-21", "tomorrow", null, "to must be a date")]
    [InlineData("2026-09-21", "2026-09-20", null, "to must not be before from")]
    [InlineData("2026-01-01", "2027-01-02", null, "at most 366 days")]
    [InlineData("2026-09-21", "2026-09-22", "6am", "dayStart must be")]
    [InlineData("2026-09-21", "2026-09-22", "24:00", "dayStart must be")]
    public void DaysRange_Invalid_SaysWhatIsWrong(string? from, string? to, string? dayStart, string expected)
    {
        string? problem = LocalApiRuns.TryReadDaysRange(from, to, dayStart, Now, out _, out _, out _);

        Assert.Contains(expected, problem);
    }

    [Fact]
    public void DaysRange_LeapYearSpan_IsAllowedAndToDefaultsToTheCurrentDay()
    {
        Assert.Null(LocalApiRuns.TryReadDaysRange("2024-01-01", "2024-12-31", "06:00", Now, out _, out _, out _));

        Assert.Null(LocalApiRuns.TryReadDaysRange("2026-09-20", null, "06:00", new DateTime(2026, 9, 22, 3, 0, 0),
            out _, out DateOnly to, out TimeOnly start));
        Assert.Equal(new DateOnly(2026, 9, 21), to);
        Assert.Equal(new TimeOnly(6, 0), start);
    }
}
