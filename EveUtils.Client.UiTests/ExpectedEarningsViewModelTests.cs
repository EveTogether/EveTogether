using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-516: the EXPECTED EARNINGS panel of the run window. The source is a double the test drives step by step, so the
/// order things arrive in (own runs first, then a server's figure, or a late answer for a site already left) is the
/// thing under test.
/// </summary>
public sealed class ExpectedEarningsViewModelTests
{
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
    private static readonly ActivityStatsKey Hideaway = ActivityStatsKey.Site(SiteTypeSource.Site, 2400);
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public async Task OwnRuns_ShowAtOnce_AndAServersFigureReplacesThemWhenItAnswers()
    {
        var source = new ScriptedSource { Own = Own(runs: 8), SharedGate = new TaskCompletionSource() };
        source.Shared = new SharedEarningsAttempt(Shared(runs: 30, pilots: 9), OwnRunsReason.None);
        var panel = new ExpectedEarningsViewModel(source);

        Task loading = panel.TrackAsync(Sanctum, 1);

        Assert.Equal(ExpectedEarningsState.Figures, panel.State);
        Assert.Equal("YOUR RUNS", panel.SourceChip);
        Assert.False(panel.IsSharedSource);
        Assert.Contains("8 of your runs", panel.BasisText);

        source.SharedGate.SetResult();
        await loading;

        Assert.Equal("ALL PILOTS · Krah Server", panel.SourceChip);
        Assert.True(panel.IsSharedSource);
        Assert.Contains("30 runs · 9 pilots · last 90 days", panel.BasisText);
        Assert.False(panel.HasNote);
    }

    /// <summary>No number below the minimum: only how far off the pilot is. Counter-proof: showing the typical figure
    /// whenever there is one at all would print a median of three runs.</summary>
    [Fact]
    public async Task TooFewRuns_ShowsNoNumber_OnlyHowManyAreNeeded()
    {
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 3) });

        await panel.TrackAsync(Sanctum, null);

        Assert.Equal(ExpectedEarningsState.TooFewRuns, panel.State);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", panel.TooFewText);
        Assert.Equal(string.Empty, panel.TypicalText);
        Assert.Equal(string.Empty, panel.BandText);
        Assert.Equal(string.Empty, panel.CompactLine);
        Assert.Equal([true, true, true, false, false], panel.RunDots);
        Assert.False(panel.HasFigures);
    }

    [Fact]
    public async Task NoKey_HidesThePanel()
    {
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 8) });
        await panel.TrackAsync(Sanctum, null);
        Assert.True(panel.IsShown);

        await panel.TrackAsync(null, null);

        Assert.False(panel.IsShown);
        Assert.Equal(ExpectedEarningsState.Hidden, panel.State);
    }

    /// <summary>A copied signature for another site while the first one is still being read: the late answer must not
    /// land on the new site's panel.</summary>
    [Fact]
    public async Task ALateAnswerForASiteAlreadyLeft_NeverOverwritesTheNewOne()
    {
        var slow = new TaskCompletionSource<ExpectedEarnings>();
        var source = new ScriptedSource { OwnFor = key => key == Sanctum ? slow.Task : Task.FromResult(Own(runs: 12)) };
        var panel = new ExpectedEarningsViewModel(source);

        Task first = panel.TrackAsync(Sanctum, null);
        await panel.TrackAsync(Hideaway, null);
        string forHideaway = panel.TypicalText;
        slow.SetResult(Own(runs: 40, perRun: 900_000_000m));
        await first;

        Assert.Equal(Hideaway, panel.Key);
        Assert.Equal(forHideaway, panel.TypicalText);
        Assert.NotEqual(string.Empty, panel.TypicalText);
        Assert.Contains("12 of your runs", panel.BasisText);
    }

    [Fact]
    public async Task TheSameKeyAgain_IsNotLookedUpAgain_AndAServerFigureIsKeptPerKey()
    {
        var source = new ScriptedSource { Own = Own(runs: 8) };
        source.Shared = new SharedEarningsAttempt(Shared(runs: 30, pilots: 9), OwnRunsReason.None);
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, 1);
        await panel.TrackAsync(Sanctum, 1);
        await panel.TrackAsync(Hideaway, 1);
        await panel.TrackAsync(Sanctum, 1);

        Assert.Equal(2, source.OwnCalls);
        Assert.Equal(2, source.SharedCalls);
        Assert.Equal("ALL PILOTS · Krah Server", panel.SourceChip);
    }

    [Theory]
    [InlineData(OwnRunsReason.NotSharing, "Turn on sharing in Settings")]
    [InlineData(OwnRunsReason.TooFewPilots, "Too few pilots share this site")]
    [InlineData(OwnRunsReason.TooFewSharedRuns, "have too few runs yet")]
    [InlineData(OwnRunsReason.ServerUnavailable, "could not be reached")]
    public async Task WhenTheOwnRunsStandIn_ThePanelSaysWhy(OwnRunsReason reason, string expected)
    {
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(null, reason) };
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, 1);

        Assert.True(panel.HasNote);
        Assert.Contains(expected, panel.NoteText);
        Assert.Equal("YOUR RUNS", panel.SourceChip);
    }

    [Fact]
    public async Task WithNoServerCoupled_ThereIsNothingToExplain()
    {
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(null, OwnRunsReason.None) };
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, 1);

        Assert.False(panel.HasNote);
    }

    [Fact]
    public async Task WithoutAPilot_TheServerIsNotAsked()
    {
        var source = new ScriptedSource { Own = Own(runs: 8) };
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, null);

        Assert.Equal(0, source.SharedCalls);
    }

    /// <summary>The current run beyond the range sits on the edge and says so, instead of drawing off the bar.</summary>
    [Fact]
    public async Task TheCurrentRunMarker_FollowsTotalIsk_AndIsClampedToTheEdges()
    {
        ExpectedEarnings own = Own(runs: 25);
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = own });
        await panel.TrackAsync(Sanctum, null);
        decimal best = own.Figures.Best!.YieldIsk;
        decimal worst = own.Figures.Worst!.YieldIsk;

        panel.UpdateCurrentIsk(null);
        Assert.False(panel.HasCurrent);

        panel.UpdateCurrentIsk(0);
        Assert.False(panel.HasCurrent);

        panel.UpdateCurrentIsk((best + worst) / 2);
        double middle = panel.CurrentMark!.Value;
        Assert.InRange(middle, 0.01, 0.99);
        Assert.DoesNotContain("above", panel.CurrentText);

        panel.UpdateCurrentIsk(best * 3);
        Assert.Equal(1d, panel.CurrentMark);
        Assert.Contains("▲ above best", panel.CurrentText);

        panel.UpdateCurrentIsk(worst / 2);
        Assert.Equal(0d, panel.CurrentMark);
        Assert.Contains("▼ below worst", panel.CurrentText);

        panel.UpdateCurrentIsk(best / 4);
        Assert.True(panel.CurrentMark < middle);
    }

    [Fact]
    public async Task AFleet_AlsoReadsPerPilot()
    {
        ExpectedEarnings own = Own(runs: 8, perRun: 40_000_000m);
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = own });
        await panel.TrackAsync(Sanctum, null);
        Assert.False(panel.HasPerPilot);

        panel.UpdateFleetSize(4);

        Assert.True(panel.HasPerPilot);
        Assert.Equal($"per pilot ≈ {IskFormat.Compact(own.Figures.Typical!.Value / 4)}", panel.PerPilotText);

        panel.UpdateFleetSize(1);
        Assert.False(panel.HasPerPilot);
    }

    [Fact]
    public async Task TheBand_FollowsHowManyRunsCarryIt()
    {
        var few = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 8) });
        var many = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 25) });

        await few.TrackAsync(Sanctum, null);
        await many.TrackAsync(Sanctum, null);

        Assert.Equal("HALF OF RUNS", few.BandLabel);
        Assert.True(few.HasConfidenceText);
        Assert.Equal("8 OF 10 RUNS", many.BandLabel);
        Assert.False(many.HasConfidenceText);
        Assert.Matches(@"^Typical \S+ · \S+–\S+$", many.HeaderSummary);
        Assert.Equal(many.HeaderSummary, many.CompactLine);
    }

    [Fact]
    public async Task OlderData_IsLabelledAsSuch()
    {
        var older = Shared(runs: 9, pilots: 3, EarningsWindow.Extended);
        var source = new ScriptedSource { Own = Own(runs: 2), Shared = new SharedEarningsAttempt(older, OwnRunsReason.None) };
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, 1);

        Assert.Contains("older data · last 365 days", panel.BasisText);
    }

    [Fact]
    public async Task ASourceThatFails_HidesThePanel_WithoutThrowing()
    {
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { OwnFor = _ => throw new InvalidOperationException("boom") });

        await panel.TrackAsync(Sanctum, null);

        Assert.False(panel.IsShown);
    }

    [Fact]
    public async Task TheLootTableButton_IsOffScreenWhereNoLootTableIsRegistered()
    {
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 8) });
        await panel.TrackAsync(Sanctum, null);

        Assert.False(panel.IsLootTableShown);
        panel.OpenLootTableCommand.Execute(null);
    }

    /// <summary>One click from the panel to the table of this site (ET-519): the button is there once the panel knows the
    /// activity, also for a folded panel, opens that activity with the pilot's character and goes with the activity.</summary>
    [Fact]
    public async Task TheLootTableButton_OpensTheTableOfItsActivity_InOneClick()
    {
        var opener = new LootTableViewModelTests.RecordingOpener();
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 8) }, lootTables: opener);
        Assert.False(panel.IsLootTableShown);
        await panel.TrackAsync(Sanctum, 90000001);
        panel.IsExpanded = false;

        Assert.True(panel.IsLootTableShown);
        panel.OpenLootTableCommand.Execute(null);

        (ActivityStatsKey? key, IReadOnlyList<int> characters) = Assert.Single(opener.Opened);
        Assert.Equal(Sanctum, key);
        Assert.Equal([90000001], characters);

        await panel.TrackAsync(null, null);
        Assert.False(panel.IsLootTableShown);
    }

    /// <summary>ET-521: DETAILS and a click on the panel are one command, and it opens the figures of the activity the panel is
    /// on; nothing opens before the panel knows one, or where no screen is registered. The sharing link opens Settings.</summary>
    [Fact]
    public async Task TheDetailsCommand_OpensTheFiguresOfItsActivity_AndTheSharingLinkOpensSettings()
    {
        var screens = new RecordingEarningsScreens();
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(null, OwnRunsReason.NotSharing) };
        var panel = new ExpectedEarningsViewModel(source, screens: screens);
        Assert.False(panel.IsDetailsShown);
        panel.OpenDetailsCommand.Execute(null);
        Assert.Empty(screens.Opened);

        await panel.TrackAsync(Sanctum, 90000001);
        panel.OpenDetailsCommand.Execute(null);
        Assert.True(panel.IsDetailsShown);
        Assert.Equal([Sanctum], screens.Opened);

        Assert.True(panel.ShowsSharingLink);
        Assert.False(panel.ShowsPlainNote);
        Assert.Equal("Showing your own runs. Turn on sharing in Settings to include the other pilots' runs.", panel.NoteText);
        Assert.Equal(panel.NoteText, panel.SharingNoteLead + panel.SharingLinkText + panel.SharingNoteTail);
        panel.TurnOnSharingCommand.Execute(null);
        Assert.Equal(1, screens.SharingSettingsOpened);

        Assert.False(new ExpectedEarningsViewModel(source).ShowsSharingLink);
    }

    [Fact]
    public async Task AnyOtherReason_StaysPlainText_WithNoLink()
    {
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(null, OwnRunsReason.TooFewPilots) };
        var panel = new ExpectedEarningsViewModel(source, screens: new RecordingEarningsScreens());

        await panel.TrackAsync(Sanctum, 90000001);

        Assert.False(panel.ShowsSharingLink);
        Assert.True(panel.ShowsPlainNote);
    }

    /// <summary>ET-521: the source chip stays when there are too few runs for a number, so the dots say whose runs they count.
    /// Counter-proof: the chip needing figures hides it here.</summary>
    [Fact]
    public async Task TooFewRuns_KeepTheSourceChip()
    {
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 3) });

        await panel.TrackAsync(Sanctum, null);

        Assert.True(panel.IsTooFewRuns);
        Assert.True(panel.ShowsPlainSourceChip);
        Assert.Equal("YOUR RUNS", panel.SourceChip);
    }

    /// <summary>ET-521: "updated … ago" counts from when the figures were read and moves on with the window's tick.</summary>
    [Fact]
    public async Task TheUpdatedLine_SaysHowOldTheFiguresAre_AndMovesOn()
    {
        var time = new SettableTime(Now);
        var panel = new ExpectedEarningsViewModel(new ScriptedSource { Own = Own(runs: 8) with { ReadAtUtc = Now } }, time: time);

        await panel.TrackAsync(Sanctum, null);
        Assert.Equal("updated just now", panel.UpdatedText);

        time.Advance(TimeSpan.FromSeconds(150));
        panel.RefreshAge();
        Assert.Equal("updated 2 min ago", panel.UpdatedText);

        await panel.TrackAsync(null, null);
        Assert.Equal(string.Empty, panel.UpdatedText);
    }

    private sealed class SettableTime(DateTime nowUtc) : TimeProvider
    {
        private DateTime _nowUtc = nowUtc;

        public override DateTimeOffset GetUtcNow() => new(_nowUtc, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _nowUtc += by;
    }

    [Fact]
    public void TheLogScale_KeepsOrder_AndPinsValuesOutsideTheRangeToTheEdges()
    {
        var scale = new ExpectedEarningsViewModel.LogScale(2_000_000m, 600_000_000m);

        Assert.Equal(0d, scale.Position(1m));
        Assert.Equal(1d, scale.Position(5_000_000_000m));
        Assert.True(scale.Position(10_000_000m) < scale.Position(40_000_000m));
        Assert.True(scale.Position(40_000_000m) < scale.Position(400_000_000m));
        Assert.InRange(scale.Position(40_000_000m), 0.2, 0.8);
    }

    /// <summary>Several servers answered (ET-512 decision 10): the source chip offers them, a pick shows that server's figure at
    /// once and is remembered. Counter-proof: leave the pick unremembered and the last assertion goes red.</summary>
    [Fact]
    public async Task SeveralServersAnswered_TheChipOffersThem_AndAPickIsShownAndRemembered()
    {
        var picks = new RecordingPicks();
        ExpectedEarnings krah = Shared(runs: 30, pilots: 9) with { ServerAddress = "https://krah.invalid" };
        ExpectedEarnings alliance = Shared(runs: 12, pilots: 3) with { ServerName = "Alliance Server", ServerAddress = "https://alliance.invalid" };
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(krah, OwnRunsReason.None, [krah, alliance]) };
        var panel = new ExpectedEarningsViewModel(source, picks: picks);
        await panel.TrackAsync(Sanctum, 1);

        Assert.True(panel.HasServerChoice);
        Assert.True(panel.ShowsChoiceSourceChip);
        Assert.False(panel.ShowsPlainSourceChip);
        Assert.False(panel.IsServerListOpen);
        panel.ToggleServerListCommand.Execute(null);
        Assert.True(panel.IsServerListOpen);

        await panel.ServerOptions.Single(option => !option.IsCurrent).ChooseCommand.ExecuteAsync(null);

        Assert.Equal("ALL PILOTS · Alliance Server", panel.SourceChip);
        Assert.Contains("12 runs · 3 pilots", panel.BasisText);
        Assert.False(panel.IsServerListOpen);
        Assert.Equal(["https://alliance.invalid"], picks.Set);

        // Left and come back to the same site: the pick is still the one shown.
        await panel.TrackAsync(null, null);
        await panel.TrackAsync(Sanctum, 1);
        Assert.Equal("ALL PILOTS · Alliance Server", panel.SourceChip);
    }

    [Fact]
    public async Task OneServer_KeepsThePlainChip_AndNoList()
    {
        var source = new ScriptedSource { Own = Own(runs: 8), Shared = new SharedEarningsAttempt(Shared(runs: 30, pilots: 9), OwnRunsReason.None) };
        var panel = new ExpectedEarningsViewModel(source);

        await panel.TrackAsync(Sanctum, 1);
        panel.ToggleServerListCommand.Execute(null);

        Assert.False(panel.HasServerChoice);
        Assert.True(panel.ShowsPlainSourceChip);
        Assert.False(panel.ShowsChoiceSourceChip);
        Assert.False(panel.IsServerListOpen);
    }

    private static ExpectedEarnings Own(int runs, decimal perRun = 40_000_000m) =>
        new(EarningsBasis.YourRuns, Figures(runs, perRun, EarningsWindow.Recent), 1);

    private static ExpectedEarnings Shared(int runs, int pilots, EarningsWindow window = EarningsWindow.Recent) =>
        new(EarningsBasis.AllPilots, Figures(runs, 55_000_000m, window), pilots, "Krah Server");

    /// <summary>Real figures from the statistic itself, so the panel is fed the shape production hands it.</summary>
    private static EarningsFiguresDto Figures(int runs, decimal perRun, EarningsWindow window)
    {
        EarningsSample[] samples = [.. Enumerable.Range(0, runs).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index % 20}",
            CompletedAtUtc = Now.AddDays(-1 - index % 60),
            DurationSeconds = 900 + index * 30,
            FixedIsk = perRun * (0.2m + index % 7 / 4m),
            LootBasis = LootBasis.CargoDifference
        })];
        return EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), Now, window);
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

    private sealed class ScriptedSource : IExpectedEarningsSource
    {
        public ExpectedEarnings? Own { get; init; }
        public Func<ActivityStatsKey, Task<ExpectedEarnings>>? OwnFor { get; init; }
        public SharedEarningsAttempt Shared { get; set; } = new(null, OwnRunsReason.None);
        public TaskCompletionSource? SharedGate { get; init; }
        public int OwnCalls { get; private set; }
        public int SharedCalls { get; private set; }

        public Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default)
        {
            OwnCalls++;
            return OwnFor?.Invoke(key) ?? Task.FromResult(Own ?? throw new InvalidOperationException("No own figure scripted."));
        }

        public async Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId,
            CancellationToken cancellationToken = default, string? activityRef = null, string? onlyServer = null)
        {
            SharedCalls++;
            if (SharedGate is not null)
                await SharedGate.Task;
            return Shared;
        }
    }
}
