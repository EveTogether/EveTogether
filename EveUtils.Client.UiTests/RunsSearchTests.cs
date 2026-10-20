using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-518: the search over the runs overview. What the ticket pins — "sleeper" finds a site by its faction, "crimson" by a
/// name part no catalogue knows, an uncatalogued site by its name, with the SDE gone nothing throws, an empty field is the
/// list as it was, OPSEC masks what is shown and never what is searched, filters stack, and the suggestions are capped and
/// ordered. The window's own claims (Bounds, keys, contrast) are in <see cref="RunsByActivityWindowTests"/>.
/// </summary>
public sealed class RunsSearchTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<Character> Crew = [new("Ra Vinter", 90000001)];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ── Matching ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sleeper", true)]
    [InlineData("SLEEP", true)]
    [InlineData("sleeper cache", true)]
    [InlineData("cache sleeper", true)]
    [InlineData("cache superior", true)]
    [InlineData("leeper", false)]
    [InlineData("sleeper enclave", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void EveryWord_MustStartAWord_InAnyOrder_IgnoringCase(string term, bool expected)
    {
        Assert.Equal(expected, ActivitySearchTerms.Matches("Superior Sleeper Cache Sleepers combat site", ActivitySearchTerms.Words(term)));
    }

    /// <summary>"Crimson" is a part of a name and no faction: counter-proof, search only the faction and family and it is
    /// not found.</summary>
    [Fact]
    public void ACatalogueSite_IsFoundByFaction_AndAnyNamePart_ByNameAlone()
    {
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(5001, "Superior Sleeper Cache", 11, "Sleeper Cache", 22, "Sleepers", null, 5, false, []))
            .AddSite(new SdeSite(5002, "Crimson Hideaway", 12, "Hideaway", 23, "Blood Raiders", null, 3, false, []));
        var facts = new RunRowFacts(sde);

        ActivitySearchProfile cache = _ProfileOf(facts, _Dto(ActivityKind.Site, "Superior Sleeper Cache", 5001));
        ActivitySearchProfile crimson = _ProfileOf(facts, _Dto(ActivityKind.Site, "Crimson Hideaway", 5002));

        Assert.Equal("Sleepers", cache.Faction);
        Assert.Equal("Sleeper Cache", cache.Archetype);
        Assert.True(_Finds(cache, "sleepers"));
        Assert.True(_Finds(cache, "DED 5"));
        Assert.True(_Finds(crimson, "crimson"));
        Assert.True(_Finds(crimson, "blood"));
        Assert.False(_Finds(crimson, "sleeper"));
        Assert.False(_Finds(cache, "crimson"));
        Assert.False(_Finds(new ActivitySearchProfile("Crimson Hideaway", null, null, "Combat Site"), "blood"));
    }

    /// <summary>An uncatalogued site has only its name and its type; without the SDE every site is that. Counter-proof: look
    /// the faction up unconditionally and the offline case throws or finds a faction that was never there.</summary>
    [Fact]
    public void AnUncataloguedSite_AndASiteWithoutTheSde_AreFoundByNameAndType_WithoutAnError()
    {
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(5001, "Superior Sleeper Cache", 11, "Sleeper Cache", 22, "Sleepers", null, 5, false, []));
        ActivityOverviewRowDto uncatalogued = _Dto(ActivityKind.Site, "Crimson Hideaway", 0);
        ActivityOverviewRowDto cataloguedButOffline = _Dto(ActivityKind.Site, "Superior Sleeper Cache", 5001);

        ActivitySearchProfile named = _ProfileOf(new RunRowFacts(sde), uncatalogued);
        ActivitySearchProfile offline = _ProfileOf(new RunRowFacts(sde.Offline()), cataloguedButOffline);
        ActivitySearchProfile noSde = _ProfileOf(new RunRowFacts(null), cataloguedButOffline);

        Assert.Null(named.Faction);
        Assert.True(_Finds(named, "crimson"));
        Assert.Null(offline.Faction);
        Assert.Null(noSde.Archetype);
        Assert.True(_Finds(offline, "cache"));
        Assert.True(_Finds(noSde, "superior"));
        Assert.False(_Finds(offline, "sleepers"));
    }

    // ── The search view model ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Typing filters after 150 ms of quiet: two quick keys apply once, with the last word. Clearing does not wait.
    /// Counter-proof: apply on every key and the count goes to two before the delay is out.</summary>
    [Fact]
    public async Task Typing_AppliesOnceAfterTheDebounce_AndClearingAppliesAtOnce()
    {
        int applied = 0;
        using var search = new RunsSearchViewModel(() => applied++, () => _Pool(), TimeSpan.FromMilliseconds(60));

        search.Text = "s";
        search.Text = "sleeper";

        Assert.Equal(0, applied);
        Assert.False(search.HasTerm);
        await _WaitAsync(() => applied == 1);
        Assert.Equal(["sleeper"], search.Words);

        search.Text = string.Empty;

        Assert.Equal(2, applied);
        Assert.False(search.HasTerm);
    }

    [Fact]
    public void TheSuggestions_AreFamiliesFirst_CountedOverTheList_AndCappedAtSix()
    {
        using var search = new RunsSearchViewModel(() => { }, () => _Pool(), TimeSpan.Zero);

        search.Text = "sleeper";

        SearchSuggestionViewModel first = search.Suggestions[0];
        Assert.Equal(SearchSuggestionKind.Faction, first.Kind);
        Assert.Equal("Sleepers", first.Term);
        Assert.Equal("2 runs", first.CountText);
        Assert.Equal("FACTION", first.KindText);
        Assert.Contains(search.Suggestions, suggestion => suggestion is { Kind: SearchSuggestionKind.Site, Term: "Superior Sleeper Cache", CountText: "1 run" });
        Assert.DoesNotContain(search.Suggestions, suggestion => suggestion.Term == "Angel Haven");
        Assert.True(search.ShowsSuggestions);

        var many = Enumerable.Range(1, 10).Select(index => new ActivitySearchProfile($"Sleeper Outpost {index}", null, null, "Combat Site")).ToList();
        using var crowded = new RunsSearchViewModel(() => { }, () => new SearchPool(many, "run", "runs"), TimeSpan.Zero);
        crowded.Text = "sleeper";
        Assert.Equal(RunsSearchViewModel.MaxSuggestions, crowded.Suggestions.Count);
    }

    /// <summary>A pick puts the unmarked name in the field and applies it; a term that is exactly a suggestion offers nothing
    /// more. OPSEC marks a site's suggestion on screen only — a faction is no location.</summary>
    [Fact]
    public void PickingASuggestion_FillsTheField_Applies_AndMasksOnlyTheSite()
    {
        int applied = 0;
        using var search = new RunsSearchViewModel(() => applied++, () => _Pool(), TimeSpan.Zero);
        search.Text = "crim";
        SearchSuggestionViewModel site = Assert.Single(search.Suggestions, suggestion => suggestion.Kind == SearchSuggestionKind.Site);

        Assert.True(OpsecText.IsMarked(site.DisplayText));
        Assert.False(OpsecText.IsMarked(site.Term));

        int beforePick = applied;

        search.Pick(site);

        Assert.Equal("Crimson Hideaway", search.Text);
        Assert.Equal(["Crimson", "Hideaway"], search.Words);
        Assert.Equal(beforePick + 1, applied);
        Assert.Empty(search.Suggestions);
        Assert.False(search.ShowsSuggestions);

        search.Text = "sleep";
        Assert.False(OpsecText.IsMarked(search.Suggestions.First(suggestion => suggestion.Kind == SearchSuggestionKind.Faction).DisplayText));
    }

    [Fact]
    public void TheKeys_WalkTheSuggestions_EnterTakesOne_AndClearGoesToEmpty()
    {
        using var search = new RunsSearchViewModel(() => { }, () => _Pool(), TimeSpan.Zero);
        search.Text = "sleeper";

        search.MoveSuggestion(1);
        search.MoveSuggestion(1);
        Assert.True(search.Suggestions[1].IsHighlighted);
        Assert.False(search.Suggestions[0].IsHighlighted);
        search.MoveSuggestion(-1);
        search.MoveSuggestion(-1);
        Assert.DoesNotContain(search.Suggestions, suggestion => suggestion.IsHighlighted);

        search.MoveSuggestion(1);
        string expected = search.Suggestions[0].Term;
        search.Accept();
        Assert.Equal(expected, search.Text);

        search.Clear();
        Assert.Equal(string.Empty, search.Text);
        Assert.False(search.HasTerm);
        Assert.False(search.HasText);
    }

    // ── The lists ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The RUNS list: the field narrows the rows and the range line counts only them; empty is the list as it was.
    /// Typed text finds the real name though OPSEC masks the shown one. Counter-proof: match on the shown (marked) text and
    /// "crimson" finds nothing.</summary>
    [AvaloniaFact]
    public async Task TheRunsList_IsNarrowedByTheTerm_CountsOnlyThat_AndComesBackWhole()
    {
        using var instance = TestClientInstance.Create();
        await _SaveAsync(instance, "Superior Sleeper Cache", 0);
        await _SaveAsync(instance, "Crimson Hideaway", 1);
        await _SaveAsync(instance, "Angel Haven", 2);
        await _SaveAsync(instance, "Hedbergite", 3, ActivityKind.Mining);
        RunsOverviewViewModel viewModel = await _OpenAsync(instance);
        int all = _RowsOf(viewModel).Count;
        Assert.Equal(4, all);
        string countAtRest = viewModel.RangeCountText;

        _Search(viewModel, "crimson");
        ActivityOverviewRowViewModel crimson = Assert.Single(_RowsOf(viewModel));
        Assert.True(OpsecText.IsMarked(crimson.SiteText));
        Assert.Equal("Crimson Hideaway", crimson.Search.Name);
        Assert.Equal(RunsActivitySummaryText.ActivitiesCount(1), viewModel.RangeCountText);

        _Search(viewModel, "sleeper cache");
        Assert.Equal("Superior Sleeper Cache", Assert.Single(_RowsOf(viewModel)).Search.Name);

        _Search(viewModel, "mining");
        Assert.Equal("Hedbergite", Assert.Single(_RowsOf(viewModel)).Search.Name);

        _Search(viewModel, string.Empty);
        Assert.Equal(all, _RowsOf(viewModel).Count);
        Assert.Equal(countAtRest, viewModel.RangeCountText);
    }

    /// <summary>Search, TYPES and CHARACTERS stack: a term that only another type matches shows nothing, says so with the
    /// term, and one click on clear filters brings everything back. Counter-proof: clear only the term and the type that is
    /// off keeps the list short.</summary>
    [AvaloniaFact]
    public async Task FiltersStack_AndNothingLeft_SaysSo_WithAOneClickWayOut()
    {
        using var instance = TestClientInstance.Create();
        await _SaveAsync(instance, "Superior Sleeper Cache", 0);
        await _SaveAsync(instance, "Hedbergite", 1, ActivityKind.Mining);
        RunsOverviewViewModel viewModel = await _OpenAsync(instance);
        viewModel.TypeFilter.Tiles.Single(tile => tile.Name == "Mining").Toggle();
        Assert.Single(_RowsOf(viewModel));
        Assert.False(viewModel.ShowsClearFilters);

        _Search(viewModel, "hedbergite");

        Assert.Empty(_RowsOf(viewModel));
        Assert.Equal("No runs match 'hedbergite' with the current filters.", viewModel.SelectedTab?.StatusMessage);
        Assert.True(viewModel.ShowsClearFilters);

        viewModel.ClearFiltersCommand.Execute(null);

        Assert.Equal(2, _RowsOf(viewModel).Count);
        Assert.Equal(string.Empty, viewModel.Search.Text);
        Assert.All(viewModel.TypeFilter.Tiles, tile => Assert.True(tile.IsOn));
        Assert.False(viewModel.ShowsClearFilters);
    }

    /// <summary>The type tiles count what the term leaves, so "sleeper" does not leave a mining tile claiming a run.</summary>
    [AvaloniaFact]
    public async Task TheTileCounts_FollowTheTerm()
    {
        using var instance = TestClientInstance.Create();
        await _SaveAsync(instance, "Superior Sleeper Cache", 0);
        await _SaveAsync(instance, "Hedbergite", 1, ActivityKind.Mining);
        RunsOverviewViewModel viewModel = await _OpenAsync(instance);

        _Search(viewModel, "hedbergite");

        Assert.Equal(1, viewModel.TypeFilter.Tiles.Single(tile => tile.Name == "Mining").Count);
        Assert.Equal(0, viewModel.TypeFilter.Tiles.Single(tile => tile.Name != "Mining").Count);
    }

    /// <summary>BY ACTIVITY: the same field filters the activities on their unmarked search text, "n of m" says how many of
    /// how many, and the CHARACTERS tiles still do not reach the list (ET-517).</summary>
    [Fact]
    public async Task ByActivity_IsFilteredOnTheUnmarkedText_AndCountsNOfM()
    {
        var sleeper = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
        var crimson = ActivityStatsKey.Site(SiteTypeSource.Site, 2316);
        var sde = new FakeSdeAccessor()
            .AddSite(new SdeSite(2311, "Superior Sleeper Cache", 11, "Sleeper Cache", 22, "Sleepers", null, 5, false, []))
            .AddSite(new SdeSite(2316, "Crimson Hideaway", 12, "Hideaway", 23, "Blood Raiders", null, 3, false, []));
        var list = new RunsByActivityViewModel(new ScriptedSource(_Stats(sleeper, "Superior Sleeper Cache"), _Stats(crimson, "Crimson Hideaway")),
            new ActivityStatsLabels(sde, new RunRowFacts(sde)), _ => true, null, null);
        await list.ShowAsync(new ActivityStatsRequest(StartedAtUtc.AddDays(-30), StartedAtUtc, null, [90000001]));
        Assert.Equal(string.Empty, list.CountText);

        IReadOnlyList<string> words = ActivitySearchTerms.Words("sleepers");
        list.SearchFilter = row => ActivitySearchTerms.Matches(row.Label.Search.Text, words);
        list.Reapply();

        Assert.Equal(sleeper, Assert.Single(list.Items).Key);
        Assert.Equal("1 of 2", list.CountText);
        Assert.True(OpsecText.IsMarked(list.Items[0].NameText));
        Assert.False(OpsecText.IsMarked(list.Items[0].Label.Search.Text));

        list.SearchFilter = null;
        list.Reapply();
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(string.Empty, list.CountText);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static SearchPool _Pool() => new(
    [
        new ActivitySearchProfile("Superior Sleeper Cache", "Sleepers", "Sleeper Cache", "Combat Site"),
        new ActivitySearchProfile("Sleeper Enclave", "Sleepers", "Sleeper Enclave", "Relic Site"),
        new ActivitySearchProfile("Crimson Hideaway", null, null, "Combat Site"),
        new ActivitySearchProfile("Angel Haven", "Angel Cartel", "Haven", "Combat Site")
    ], "run", "runs");

    private static bool _Finds(ActivitySearchProfile profile, string term) =>
        ActivitySearchTerms.Matches(profile.Text, ActivitySearchTerms.Words(term));

    private static ActivitySearchProfile _ProfileOf(RunRowFacts facts, ActivityOverviewRowDto row) =>
        facts.SearchProfileOf(row, facts.TypeOf(row.ActivityKind, row.SignatureGroupSnapshot, row.SiteTypeId, row.SiteName));

    private static ActivityOverviewRowDto _Dto(ActivityKind kind, string siteName, int siteTypeId) =>
        new(Guid.NewGuid(), null, Guid.NewGuid(), kind, siteName, null, siteTypeId, null, StartedAtUtc, 600, 1, 1, [], [], 0m, null,
            0, false, false, [], IskBreakdown.None, IskBreakdown.None, true, []);

    private static async Task _WaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20, Token);

        Assert.True(condition(), "the condition was not met in time");
    }

    private static void _Search(RunsOverviewViewModel viewModel, string term)
    {
        viewModel.Search.Text = term;
        viewModel.Search.ApplyNow();
    }

    private static List<ActivityOverviewRowViewModel> _RowsOf(RunsOverviewViewModel viewModel) =>
        [.. viewModel.Tabs[0].Days.SelectMany(day => day.Rows)];

    private static async Task _SaveAsync(TestClientInstance instance, string siteName, int hour, ActivityKind kind = ActivityKind.Site)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        DateTime startedAt = StartedAtUtc.AddHours(hour);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, kind, startedAt, 0, siteName, 30000142), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAt.AddMinutes(15), startedAt.AddMinutes(16), [], [], [], []), Token);
    }

    private static async Task<RunsOverviewViewModel> _OpenAsync(TestClientInstance instance)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Token);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services, Crew, runClock: false,
            paneReadDelay: TimeSpan.Zero, time: RunsTestClock.Fixed);
        await viewModel.LoadAsync(Token);
        return viewModel;
    }

    private static ActivityStats _Stats(ActivityStatsKey key, string name)
    {
        EarningsSample[] samples = [.. Enumerable.Range(1, 25).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index % 4}", CapWeight = 1, CompletedAtUtc = StartedAtUtc.AddDays(-index), DurationSeconds = 900,
            FixedIsk = 10_000_000m * (0.2m + index % 9 / 4m), LootBasis = LootBasis.CargoDifference
        })];
        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), StartedAtUtc);
        var basis = new ExpectedEarnings(EarningsBasis.AllPilots, figures, 4, "Krah Server", "https://krah.invalid");
        return new ActivityStats(key, name, StartedAtUtc.AddDays(-2), figures, basis, new ExpectedEarnings(EarningsBasis.YourRuns, figures, 1),
            OwnRunsReason.None, [basis], figures.EffectiveRunCount, StartedAtUtc);
    }

    private sealed class ScriptedSource(params ActivityStats[] stats) : IActivityStatsSource
    {
        public Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityStatsOverview(stats, null));

        public Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(stats.First(known => known.Key == key));
    }
}
