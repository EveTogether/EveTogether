using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-517: the BY ACTIVITY list, its rows and its detail, read off scripted figures. What the ticket pins — no number
/// under the minimum, the shared figure beside the own runs, a row without a figure last whichever way it sorts, the
/// selection kept by key through a refresh, a masked site name, and the detail's distribution, best and worst runs.
/// </summary>
public sealed class RunsByActivityTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
    private static readonly ActivityStatsKey Cache = ActivityStatsKey.Site(SiteTypeSource.Site, 2316);
    private static readonly ActivityStatsKey Wreck = ActivityStatsKey.Site(SiteTypeSource.Site, 2400);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ARowUnderTheMinimum_ShowsADashAndHowFarOff_NeverANumber()
    {
        ActivityStatsRowViewModel row = _Row(_Stats(Cache, runs: 3));

        Assert.Equal("—", row.TypicalText);
        Assert.Equal("3 of 5 needed", row.TypicalSub);
        Assert.Equal("—", row.PerHourText);
        Assert.False(row.HasBand);
        Assert.Equal("—", row.YoursText);
        Assert.Equal("3 of 5 needed", row.YoursSub);
        Assert.Null(row.TypicalValue);
    }

    /// <summary>The server's figure is the headline and the pilot's own runs stay as YOURS; the row names its basis.</summary>
    [Fact]
    public void ASharedRow_ReadsTheServersFigure_AndKeepsYoursBeside()
    {
        ActivityStatsRowViewModel row = _Row(_Stats(Sanctum, runs: 25, shared: true, yoursRuns: 8));

        Assert.True(row.IsSharedBasis);
        Assert.Equal("25 runs · 4 pilots", row.TypicalSub);
        Assert.StartsWith("All pilots", row.BasisTooltip);
        Assert.True(row.HasBand);
        Assert.InRange(row.BandStart, 0d, row.TypicalMark);
        Assert.InRange(row.BandEnd, row.TypicalMark, 1d);
        Assert.Contains(" – ", row.BandText);
        Assert.EndsWith("/h", row.PerHourText);
        Assert.Equal("8 runs", row.YoursSub);
        Assert.True(row.ShowsYours);
    }

    /// <summary>Without a server the figure is the pilot's own and the row says so, with the reason on hover.</summary>
    [Fact]
    public void AnOwnRunsRow_SaysSo_AndWhy()
    {
        ActivityStatsRowViewModel row = _Row(_Stats(Sanctum, runs: 12, reason: OwnRunsReason.NotSharing));

        Assert.False(row.IsSharedBasis);
        Assert.StartsWith("your runs", row.TypicalSub);
        Assert.Contains("Turn on sharing in Settings", row.BasisTooltip);
    }

    /// <summary>OPSEC (ET-417): the site's name is marked, the search text is not, and nothing else on the row carries a
    /// marker. Counter-proof: show the raw name and the first assertion goes red.</summary>
    [Fact]
    public void TheSiteName_IsMarked_TheSearchTextIsNot()
    {
        ActivityStatsRowViewModel row = _Row(_Stats(Sanctum, runs: 12, name: "Angel Sanctum"));

        Assert.Equal(OpsecText.Mark("Angel Sanctum"), row.NameText);
        Assert.Contains("Angel Sanctum", row.Label.SearchText);
        Assert.False(OpsecText.IsMarked(row.Label.SearchText));
        Assert.False(OpsecText.IsMarked(row.MetaText));
        Assert.False(OpsecText.IsMarked(row.TypicalSub));
    }

    /// <summary>Default order: last flown by the pilot first. Counter-proof: order by name and the check below fails.</summary>
    [Fact]
    public async Task TheDefaultOrder_IsTheLastFlownFirst()
    {
        RunsByActivityViewModel list = _List(_Stats(Sanctum, 25, flownDaysAgo: 5), _Stats(Cache, 25, flownDaysAgo: 1), _Stats(Wreck, 25, flownDaysAgo: 3));

        await list.ShowAsync(_Request());

        Assert.Equal([Cache, Wreck, Sanctum], list.Items.Select(row => row.Key));
    }

    /// <summary>A row with no figure sorts under every row that has one — in both directions, since "—" is not the smallest
    /// number. Counter-proof: sort a null as zero and the ascending order puts it first.</summary>
    [Fact]
    public async Task ARowWithoutAFigure_SortsLast_WhicheverWayTheColumnRuns()
    {
        RunsByActivityViewModel list = _List(_Stats(Sanctum, 25, baseIsk: 50_000_000m), _Stats(Cache, 3), _Stats(Wreck, 25, baseIsk: 10_000_000m));
        await list.ShowAsync(_Request());

        list.SortByTypicalCommand.Execute(null);
        Assert.Equal([Sanctum, Wreck, Cache], list.Items.Select(row => row.Key));
        Assert.True(list.IsDescending);

        list.SortByTypicalCommand.Execute(null);
        Assert.False(list.IsDescending);
        Assert.Equal([Wreck, Sanctum, Cache], list.Items.Select(row => row.Key));
        Assert.Equal(" ▴", list.TypicalMark);
        Assert.Equal(string.Empty, list.LastFlownMark);
    }

    [Fact]
    public async Task SortingByRuns_PutsTheMostRunsBehindTheFigureFirst()
    {
        RunsByActivityViewModel list = _List(_Stats(Sanctum, 8), _Stats(Cache, 30), _Stats(Wreck, 12));
        await list.ShowAsync(_Request());

        list.SortByRunsCommand.Execute(null);

        Assert.Equal([Cache, Wreck, Sanctum], list.Items.Select(row => row.Key));
    }

    [Fact]
    public async Task SortingByPerHour_UsesTheMedianOfEachRunsOwnPerHour()
    {
        RunsByActivityViewModel list = _List(_Stats(Sanctum, 25, baseIsk: 10_000_000m, minutes: 5), _Stats(Cache, 25, baseIsk: 50_000_000m, minutes: 60));
        await list.ShowAsync(_Request());

        list.SortByPerHourCommand.Execute(null);

        Assert.Equal([Sanctum, Cache], list.Items.Select(row => row.Key));
    }

    /// <summary>The TYPES tiles and the search that ET-518 adds both filter the rows; the rows are not read again.
    /// Counter-proof: ignore the filter and all three rows stay.</summary>
    [Fact]
    public async Task TheTypesFilter_AndTheSearchHook_NarrowTheRows_WithoutAReread()
    {
        bool typeShown = true;
        var source = new ScriptedSource(_Stats(Sanctum, 25, name: "Angel Sanctum"), _Stats(Cache, 25, name: "Crimson Cache"), _Stats(Wreck, 25, name: "Wreck"));
        RunsByActivityViewModel list = new(source, _Labels(), _ => typeShown, null, null);
        await list.ShowAsync(_Request());
        int reads = source.Reads;
        Assert.Equal(3, list.Items.Count);

        list.SearchFilter = row => row.Label.SearchText.Contains("crimson", StringComparison.OrdinalIgnoreCase);
        list.Reapply();
        Assert.Equal([Cache], list.Items.Select(row => row.Key));

        list.SearchFilter = null;
        typeShown = false;
        list.Reapply();
        Assert.Empty(list.Items);
        Assert.True(list.HasNoRows);
        Assert.Equal("No activity matches the current filters.", list.EmptyText);
        Assert.Equal(3, list.TotalCount);
        Assert.Equal(reads, source.Reads);
    }

    /// <summary>The selection hangs on the key: a refresh that builds new rows keeps the same activity selected and its
    /// detail shown. Counter-proof: select by instance and the new rows lose it.</summary>
    [Fact]
    public async Task TheSelection_SurvivesARefresh_ByKey()
    {
        var source = new ScriptedSource(_Stats(Sanctum, 25), _Stats(Cache, 25));
        RunsByActivityViewModel list = new(source, _Labels(), _ => true, null, null);
        await list.ShowAsync(_Request());
        ActivityStatsRowViewModel chosen = list.Items.Single(row => row.Key == Cache);
        list.Select(chosen);
        Assert.True(list.Detail.HasRow);

        source.Replace(_Stats(Sanctum, 26), _Stats(Cache, 26));
        await list.ShowAsync(_Request());

        Assert.NotSame(chosen, list.SelectedRow);
        Assert.Equal(Cache, list.SelectedRow?.Key);
        Assert.Equal(Cache, list.Detail.Key);
    }

    /// <summary>A click on the row that is already selected still asks for its pane — how a closed drawer opens again.</summary>
    [Fact]
    public async Task ClickingTheSelectedRowAgain_AsksForItsPaneAgain()
    {
        RunsByActivityViewModel list = _List(_Stats(Sanctum, 25));
        await list.ShowAsync(_Request());
        int asked = 0;
        list.RowSelected += _ => asked++;

        list.Items[0].SelectCommand.Execute(null);
        list.Items[0].SelectCommand.Execute(null);

        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task AServerTab_HidesYours_AndShowsTheServersWords()
    {
        var source = new ScriptedSource { Status = "Turn on sharing in Settings to see what this server's pilots earn." };
        RunsByActivityViewModel list = new(source, _Labels(), _ => true, null, null);

        await list.ShowAsync(new ActivityStatsRequest(Now.AddDays(-30), Now, "https://krah.invalid", []));

        Assert.False(list.ShowsYoursColumn);
        Assert.True(list.HasStatus);
        Assert.False(list.HasNoRows);
        await list.ShowAsync(_Request());
        Assert.True(list.ShowsYoursColumn);
    }

    // ── The detail ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drawer's numbers, the distribution on one log scale with the pilot's own runs as dots, the real
    /// best and worst, and what it is based on. Counter-proof: a linear scale puts the typical run at the left edge.</summary>
    [Fact]
    public void TheDetail_ShowsTheFiguresTheDistributionAndTheRealBestAndWorst()
    {
        var detail = new ActivityStatsDetailViewModel(time: new FixedTime(Now));
        ActivityStatsRowViewModel row = _Row(_Stats(Sanctum, runs: 25, shared: true, yoursRuns: 6, readMinutesAgo: 2));

        detail.Show(row);

        Assert.True(detail.HasFigures);
        Assert.False(detail.IsTooFew);
        Assert.Equal("EARNINGS · SITE", detail.KindText);
        Assert.Equal("8 OF 10 RUNS", detail.BandLabel);
        EarningsDistribution shape = Assert.IsType<EarningsDistribution>(detail.Distribution);
        Assert.Equal(16, shape.Bars.Count);
        Assert.Equal(1d, shape.Bars.Max());
        Assert.InRange(shape.TypicalMark, shape.BandStart, shape.BandEnd);
        Assert.InRange(shape.TypicalMark, 0.05d, 0.95d);
        Assert.Equal(6, shape.OwnMarks.Count);
        Assert.True(detail.HasOwnDots);
        Assert.Equal(3, detail.BestRuns.Count);
        Assert.Equal(3, detail.WorstRuns.Count);
        Assert.Equal("Based on 25 runs · 4 pilots · last 90 days · updated 2 min ago", detail.FooterText);
        Assert.False(detail.IsTopDropsShown);
        Assert.False(detail.IsLootTableShown);
    }

    /// <summary>An own run opens, another pilot's names nobody and does nothing (ET-512 decision 2).</summary>
    [Fact]
    public async Task AnOwnBestRun_Opens_AndAnotherPilotsDoesNot()
    {
        List<string> opened = [];
        var detail = new ActivityStatsDetailViewModel(openRun: (activityRef, _) =>
        {
            opened.Add(activityRef);
            return Task.CompletedTask;
        });
        detail.Show(_Row(_Stats(Sanctum, runs: 25, shared: true, ownBest: true)));

        ActivityRunLineViewModel own = detail.BestRuns[0];
        ActivityRunLineViewModel other = detail.BestRuns[2];
        await own.OpenCommand.ExecuteAsync(null);

        Assert.True(own.IsOwn);
        Assert.Equal("your run ›", own.WhoText);
        Assert.False(other.IsOwn);
        Assert.Equal("another pilot", other.WhoText);
        Assert.Equal(["best-run"], opened);
        Assert.Matches(@"\d+m · \d+ \w+", own.MetaText);
    }

    /// <summary>The long-run average and the jackpot share come from 30 runs; below that the drawer says so instead.</summary>
    [Fact]
    public void TheLongRunAverage_AppearsFromThirtyRuns()
    {
        var detail = new ActivityStatsDetailViewModel();

        detail.Show(_Row(_Stats(Sanctum, runs: 25, shared: true)));
        Assert.False(detail.HasLongRunAverage);
        Assert.Contains("Shown from 30 runs (now 25)", detail.LongRunHint);

        detail.Show(_Row(_Stats(Sanctum, runs: 40, shared: true)));
        Assert.True(detail.HasLongRunAverage);
        Assert.EndsWith("ISK", detail.LongRunText);
        Assert.Contains("incl. rare drops", detail.LongRunHint);
    }

    [Fact]
    public void UnderTheMinimum_TheDetailShowsDotsAndNoNumber()
    {
        var detail = new ActivityStatsDetailViewModel();

        detail.Show(_Row(_Stats(Cache, runs: 3)));

        Assert.True(detail.IsTooFew);
        Assert.False(detail.HasFigures);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", detail.TooFewText);
        Assert.Equal([true, true, true, false, false], detail.RunDots);
        Assert.Null(detail.Distribution);
        Assert.Empty(detail.BestRuns);
    }

    /// <summary>Several servers answered: the chip lists them, a pick switches the figure and the row, and it is
    /// remembered (ET-512 decision 10). Counter-proof: leave the pick unremembered and the last assertion goes red.</summary>
    [Fact]
    public async Task APickOnTheSourceChip_SwitchesTheFigure_AndIsRemembered()
    {
        var picks = new RecordingPicks();
        var detail = new ActivityStatsDetailViewModel(picks);
        ActivityStatsRowViewModel row = _Row(_Stats(Sanctum, runs: 25, shared: true, otherServer: true));

        detail.Show(row);
        Assert.True(detail.HasServerChoice);
        Assert.True(detail.ShowsChoiceSourceChip);
        Assert.False(detail.ShowsPlainSourceChip);
        Assert.Equal("ALL PILOTS · Krah Server", detail.SourceChip);
        detail.ToggleServerListCommand.Execute(null);
        Assert.True(detail.IsServerListOpen);

        await detail.ServerOptions.Single(option => !option.IsCurrent).ChooseCommand.ExecuteAsync(null);

        Assert.Equal("ALL PILOTS · Alliance Server", detail.SourceChip);
        Assert.Equal("https://alliance.invalid", row.Basis?.ServerAddress);
        Assert.False(detail.IsServerListOpen);
        Assert.Equal(["https://alliance.invalid"], picks.Set);
    }

    [Fact]
    public void ASingleServer_KeepsThePlainChip()
    {
        var detail = new ActivityStatsDetailViewModel();

        detail.Show(_Row(_Stats(Sanctum, runs: 25, shared: true)));

        Assert.False(detail.HasServerChoice);
        Assert.True(detail.ShowsPlainSourceChip);
        Assert.False(detail.ShowsChoiceSourceChip);
    }

    // ── "Compared to typical" in the run drawer ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheComparison_NamesTheDifference_AndTheQuartileItFallsIn()
    {
        EarningsFiguresDto figures = _Figures(25, 40_000_000m);
        decimal typical = figures.Typical ?? 0m;
        decimal p75 = figures.P75 ?? 0m;
        decimal p25 = figures.P25 ?? 0m;

        Assert.EndsWith("(top 25 %)", RunComparison.Of(Sanctum, p75 + 1_000_000m, figures)?.Text);
        Assert.EndsWith("(bottom 25 %)", RunComparison.Of(Sanctum, p25 - 1_000_000m, figures)?.Text);
        Assert.Equal("Compared to typical: +0", RunComparison.Of(Sanctum, typical, figures)?.Text);
        Assert.StartsWith("Compared to typical: +1M", RunComparison.Of(Sanctum, typical + 1_000_000m, figures)?.Text);
        Assert.StartsWith("Compared to typical: -", RunComparison.Of(Sanctum, typical - 1_000_000m, figures)?.Text);
        Assert.Null(RunComparison.Of(Sanctum, 1m, _Figures(3, 40_000_000m)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static ActivityStatsRowViewModel _Row(ActivityStats stats) => new(stats, _Labels().For(stats.Key, stats.SiteName), Now);

    private static RunsByActivityViewModel _List(params ActivityStats[] stats) =>
        new(new ScriptedSource(stats), _Labels(), _ => true, null, null);

    private static ActivityStatsRequest _Request() => new(Now.AddDays(-30), Now, null, [90000001]);

    private static ActivityStatsLabels _Labels() => new(null, new RunRowFacts(null));

    private static EarningsFiguresDto _Figures(int runs, decimal baseIsk, int minutes = 15, Func<int, string?>? refOf = null)
    {
        EarningsSample[] samples = [.. Enumerable.Range(1, runs).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index % 4}",
            CompletedAtUtc = Now.AddDays(-1 - index),
            DurationSeconds = minutes * 60,
            FixedIsk = baseIsk * (0.2m + index % 9 / 4m),
            LootBasis = LootBasis.CargoDifference,
            ActivityRef = refOf?.Invoke(index),
            CapWeight = 1
        })];
        return EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), Now);
    }

    private static ActivityStats _Stats(ActivityStatsKey key, int runs, bool shared = false, int yoursRuns = 0, string? name = "Angel Sanctum",
        int flownDaysAgo = 2, decimal baseIsk = 20_000_000m, int minutes = 15, OwnRunsReason reason = OwnRunsReason.None,
        bool ownBest = false, bool otherServer = false, int readMinutesAgo = 0)
    {
        EarningsFiguresDto figures = _Figures(runs, baseIsk, minutes, index => ownBest && index % 9 == 8 ? "best-run" : null);
        var own = new ExpectedEarnings(EarningsBasis.YourRuns, yoursRuns > 0 ? _Figures(yoursRuns, baseIsk, minutes) : figures, 1);
        ExpectedEarnings basis = shared
            ? new ExpectedEarnings(EarningsBasis.AllPilots, figures, 4, "Krah Server", "https://krah.invalid")
            : own;
        List<ExpectedEarnings> answers = shared ? [basis] : [];
        if (shared && otherServer)
            answers.Add(new ExpectedEarnings(EarningsBasis.AllPilots, _Figures(runs - 4, baseIsk), 3, "Alliance Server", "https://alliance.invalid"));
        return new ActivityStats(key, name, Now.AddDays(-flownDaysAgo), yoursRuns > 0 ? _Figures(yoursRuns, baseIsk, minutes) : _Figures(runs, baseIsk, minutes),
            basis, own, shared ? OwnRunsReason.None : reason, answers, figures.EffectiveRunCount, Now.AddMinutes(-readMinutesAgo));
    }

    private sealed class ScriptedSource(params ActivityStats[] stats) : IActivityStatsSource
    {
        private ActivityStats[] _stats = stats;

        public int Reads { get; private set; }
        public string? Status { get; init; }

        public void Replace(params ActivityStats[] replacement) => _stats = replacement;

        public Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new ActivityStatsOverview(_stats, Status));
        }

        public Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(_stats.First(known => known.Key == key));
    }

    private sealed class RecordingPicks : ISharedServerPicks
    {
        public List<string> Set { get; } = [];

        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Set.LastOrDefault());

        public Task SetAsync(string serverAddress, CancellationToken cancellationToken = default)
        {
            Set.Add(serverAddress);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
