using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Client.Views.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Sdk;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-517: the runs overview with its RUNS | BY ACTIVITY switch, at the two widths the ticket names (758 floating, 1303
/// docked). Layout claims are Bounds claims: where the switch sits, that the head and the rows share their columns, that
/// the earnings open in the drawer below the breakpoint and beside the list above it, and that nothing in a row is in the
/// dim ink at rest or selected. No picture is taken.
/// </summary>
public sealed class RunsByActivityWindowTests
{
    private static readonly DateTime Evening = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Color DimInk = Color.Parse("#FF8A7E6B");
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
    private static readonly ActivityStatsKey Cache = ActivityStatsKey.Site(SiteTypeSource.Site, 2316);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<Character> Crew = [new("Ra Vinter", 90000001), new("Kav Orn", 90000002)];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel, FakeStatsSource Source);

    /// <summary>RUNS is where the screen opens and nothing is read for BY ACTIVITY until it is asked for: the usual way in
    /// costs no click and no work. The switch sits on the title row clear of the title and the fleet chip, and the range line
    /// is as tall as before it, at every width down to the window's own minimum.</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(1100d)]
    [InlineData(758d)]
    [InlineData(630d)]
    [InlineData(420d)]
    public async Task TheScreenOpensOnRuns_AndTheSwitchSitsClearOfTheRangeLine(double width)
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, width);

        Button runs = _Named<Button>(presented, "ViewRuns");
        Button byActivity = _Named<Button>(presented, "ViewByActivity");
        Assert.Contains("on", runs.Classes);
        Assert.DoesNotContain("on", byActivity.Classes);
        Assert.True(_Named<Grid>(presented, "RunsListArea").IsVisible);
        Assert.False(_Named<Grid>(presented, "StatsListArea").IsVisible);
        Assert.Equal(0, presented.Source.Reads);

        double right = presented.Content.Bounds.Width;
        List<(string Name, Rect Rect)> pieces = [.. new[]
            {
                "RangePrevious", "RangeTitle", "RangeNext", "RangeFigures", "RangeIsk", "SummaryToggle"
            }
            .Select(name => (name, _Rect(presented, _Named<Control>(presented, name))))
            .Where(piece => piece.Item2.Width > 0 && piece.Item2.Height > 0)];
        Rect switchRect = _Rect(presented, _Named<Control>(presented, "ViewSwitch"));
        Assert.True(switchRect.X >= -0.5 && switchRect.Right <= right + 0.5, $"the switch {switchRect} leaves 0..{right} at {width}");
        Assert.True(_Rect(presented, _Named<Control>(presented, "ViewRuns")).Right <= _Rect(presented, _Named<Control>(presented, "ViewByActivity")).X + 0.5);
        Assert.True(switchRect.Bottom <= _Rect(presented, _Named<Control>(presented, "RangeNavigation")).Y, $"the switch {switchRect} is not above the range line at {width}");
        foreach ((string name, Rect rect) in pieces)
            Assert.True(rect.X >= -0.5 && rect.Right <= right + 0.5, $"{name} {rect} leaves 0..{right} at {width}");
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
                Assert.False(pieces[i].Rect.Intersects(pieces[j].Rect),
                    $"{pieces[i].Name} {pieces[i].Rect} overlaps {pieces[j].Name} {pieces[j].Rect} at {width}");
    }

    /// <summary>One click on BY ACTIVITY and the list of activities is there, in a list of its own; RUNS is untouched and one
    /// click brings it back. The read is for the month on screen.</summary>
    [AvaloniaFact]
    public async Task OneClickOnByActivity_ShowsTheActivities_AndRunsComesBack()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 758);

        _Named<Button>(presented, "ViewByActivity").Command?.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 2);
        _Settle(presented);

        Assert.Contains("on", _Named<Button>(presented, "ViewByActivity").Classes);
        Assert.True(_Named<Grid>(presented, "StatsListArea").IsVisible);
        Assert.False(_Named<Grid>(presented, "RunsListArea").IsVisible);
        Assert.Equal(2, _Rows(presented).Count);
        ActivityStatsRequest request = Assert.Single(presented.Source.Requests);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), request.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), request.ToUtc);
        Assert.Null(request.ServerAddress);

        _Named<Button>(presented, "ViewRuns").Command?.Execute(null);
        _Settle(presented);
        Assert.True(_Named<Grid>(presented, "RunsListArea").IsVisible);
        Assert.False(_Named<Grid>(presented, "StatsListArea").IsVisible);
    }

    /// <summary>The month on the range line moves the figures with it. Counter-proof: leave the range out of the request and
    /// the previous month is read as September.</summary>
    [AvaloniaFact]
    public async Task TheRangeLine_MovesTheRangeTheFiguresAreReadOver()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 758);
        _Named<Button>(presented, "ViewByActivity").Command?.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => presented.Source.Requests.Count == 1);

        presented.ViewModel.PreviousCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => presented.Source.Requests.Count >= 2);

        ActivityStatsRequest last = presented.Source.Requests[^1];
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), last.FromUtc);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), last.ToUtc);
    }

    /// <summary>The head and every row share their column cells, at the floating and the docked width: what the head says
    /// is over the figure it names. The name column keeps room to read, and every row is 40 px tall.</summary>
    [AvaloniaTheory]
    [InlineData(758d)]
    [InlineData(1303d)]
    public async Task TheHeadAndTheRows_ShareTheirColumns(double width)
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, width);
        await _ShowByActivityAsync(presented);

        Control row = _Rows(presented)[0];
        Rect list = _Rect(presented, _Named<Control>(presented, "StatsList"));
        Assert.Equal(40, row.Bounds.Height);
        Assert.Equal(40, _Rect(presented, _Named<Control>(presented, "StatsRow")).Height);

        Assert.InRange(Math.Abs(_Rect(presented, _Child<Control>(row, "StatsRowYours")).X - _Rect(presented, _Named<Control>(presented, "HeadYours")).X), 0, 1);
        Assert.InRange(Math.Abs(_Rect(presented, _Child<Control>(row, "StatsRowTypical")).Right - _Rect(presented, _Named<Control>(presented, "HeadTypical")).Right), 0, 1);
        Assert.InRange(Math.Abs(_Rect(presented, _Child<Control>(row, "StatsRowRange")).X - _Rect(presented, _Named<Control>(presented, "HeadRange")).X), 0, 1);
        Assert.InRange(Math.Abs(_Rect(presented, _Child<Control>(row, "StatsRowPerHour")).X - _Rect(presented, _Named<Control>(presented, "SortPerHour")).X), 0, 1);

        Control nameColumn = _Child<TextBlock>(row, "StatsRowName").GetVisualParent<Control>()
                             ?? throw new XunitException("the name has no column");
        Assert.True(nameColumn.Bounds.Width >= 150, $"the name column is {nameColumn.Bounds.Width} wide at {width}");
        Assert.True(_Rect(presented, _Child<Control>(row, "StatsRowPerHour")).Right <= list.Right + 0.5);
        Rect bar = _Rect(presented, _Child<EarningsBandBar>(row, "StatsRowBar"));
        Assert.Equal(EarningsBandBar.CompactBarHeight, bar.Height);
        Assert.True(bar.Width > 100, $"the range bar is {bar.Width} wide");
    }

    /// <summary>Below the breakpoint a click on a row opens the earnings in the 430 px drawer, and ✕, the scrim and Esc close
    /// it with the selection standing; a click on the same row opens it again. Counter-proof: close by clearing the
    /// selection and the second open has nothing to show.</summary>
    [AvaloniaFact]
    public async Task Floating_TheEarningsOpenInTheDrawer_AndCloseWithTheSelectionStanding()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 758);
        await _ShowByActivityAsync(presented);
        ActivityStatsRowViewModel row = presented.ViewModel.ByActivity.Items[0];

        row.SelectCommand.Execute(null);
        _Settle(presented);

        Assert.True(presented.ViewModel.IsDrawerOpen);
        Assert.Equal("EARNINGS", presented.ViewModel.DrawerTitle);
        Border drawer = _Named<Border>(presented, "Drawer");
        Assert.InRange(drawer.Bounds.Width, 429, 431);
        Assert.True(_Named<Panel>(presented, "DrawerStats").IsVisible);
        Assert.Equal(OpsecText.Strip(row.NameText), _Named<TextBlock>(presented, "StatsName").Text);
        EarningsHistogram chart = _Named<EarningsHistogram>(presented, "StatsHistogram");
        Assert.InRange(chart.Bounds.Width, 300, 430 - 28 + 0.5);
        Assert.Equal(EarningsHistogram.ChartHeight, chart.Bounds.Height);

        _Named<Button>(presented, "DrawerClose").Command?.Execute(null);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.ByActivity.SelectedRow);

        row.SelectCommand.Execute(null);
        Assert.True(presented.ViewModel.IsDrawerOpen);
        _Named<Button>(presented, "ScrimClose").Command?.Execute(null);
        Assert.False(presented.ViewModel.IsDrawerOpen);

        row.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        presented.Root.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.ByActivity.SelectedRow);
    }

    /// <summary>At and above the breakpoint the earnings stand beside the list in the 400 px column and no drawer opens; ↑↓
    /// step the rows of the list.</summary>
    [AvaloniaFact]
    public async Task Docked_TheEarningsStandBesideTheList_AndTheArrowsStepTheRows()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        RunsByActivityViewModel list = presented.ViewModel.ByActivity;

        Assert.True(_Named<Panel>(presented, "PaneStats").IsVisible);
        Assert.True(_Named<TextBlock>(presented, "StatsPaneEmpty").IsVisible);

        // The screen hears a key when the focus is inside it, as docked in the app.
        _Named<Button>(presented, "SortLastFlown").Focus();
        Dispatcher.UIThread.RunJobs();
        presented.Root.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, keySymbol: null);
        _Settle(presented);

        Assert.Same(list.Items[0], list.SelectedRow);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.False(_Named<TextBlock>(presented, "StatsPaneEmpty").IsVisible);
        Rect pane = _Rect(presented, _Named<Border>(presented, "PaneHost"));
        Assert.InRange(pane.Width, 399, 401);
        Assert.InRange(_Rect(presented, _Named<EarningsHistogram>(presented, "StatsHistogram")).Width, 300, 372.5);

        presented.Root.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, keySymbol: null);
        Assert.Same(list.Items[1], list.SelectedRow);
        Assert.False(_Named<Grid>(presented, "RunsListArea").IsVisible);
    }

    /// <summary>The contrast lesson of the runs overview v2: nothing in a row is in the dim ink — at rest, hovered or selected
    /// (it measured 3.3–4.4:1 on those fills). The head is the text colour too. Counter-proof: take the row button's own
    /// foreground away and its secondary lines go back to the dim brush.</summary>
    [AvaloniaFact]
    public async Task NoTextInTheList_IsInTheDimInk_AtRestOrSelected()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);

        void AssertNoDimInk()
        {
            TextBlock[] texts =
            [
                .. _Named<Grid>(presented, "StatsListArea").GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text))
            ];
            Assert.NotEmpty(texts);
            Assert.All(texts, text =>
            {
                Color ink = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;
                Assert.NotEqual(DimInk, ink);
            });
        }

        AssertNoDimInk();
        presented.ViewModel.ByActivity.Select(presented.ViewModel.ByActivity.Items[0]);
        _Settle(presented);
        AssertNoDimInk();
        Assert.All(_Named<Panel>(presented, "PaneStats").GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)),
            text => Assert.NotEqual(DimInk, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color));
    }

    /// <summary>Without a loot table registered the LOOT column, TOP DROPS and its button are not on screen.</summary>
    [AvaloniaFact]
    public async Task WithoutALootTable_NoLootColumnAndNoTopDrops()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            _Services()(services);
            services.RemoveAll<ILootTableOpener>();
            services.RemoveAll<ILootTableSource>();
        });
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        presented.ViewModel.ByActivity.Select(presented.ViewModel.ByActivity.Items[0]);
        _Settle(presented);

        Assert.False(_Child<Button>(_Rows(presented)[0], "StatsRowLoot").IsVisible);
        Assert.False(_Named<Border>(presented, "StatsTopDrops").IsVisible);
        Assert.False(_Named<Button>(presented, "StatsLootTable").IsVisible);
        Assert.False(_Named<TextBlock>(presented, "HeadLoot").IsVisible);
        Assert.False(_Named<Button>(presented, "LootTablesButton").IsVisible);
    }

    private static Action<IServiceCollection> _ServicesWithLoot(LootTableViewModelTests.RecordingOpener opener) => services =>
    {
        _Services()(services);
        services.AddSingleton<ILootTableOpener>(opener);
        services.AddSingleton<ILootTableSource, LootTableViewModelTests.FakeSource>();
    };

    /// <summary>The LOOT column is one click to the table of that row, in the head's column and clear of ISK/H, at the
    /// floating and the docked width (ET-519). Measured: the name keeps room at 758 even with the column in.</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(758d)]
    public async Task TheLootColumn_SharesTheHeadsColumn_AndOpensTheTableOfItsRow(double width)
    {
        var opener = new LootTableViewModelTests.RecordingOpener();
        using var instance = TestClientInstance.Create(_ServicesWithLoot(opener));
        Presented presented = await _PresentAsync(instance, width);
        await _ShowByActivityAsync(presented);

        Control row = _Rows(presented)[0];
        Button loot = _Child<Button>(row, "StatsRowLoot");
        Assert.True(loot.IsVisible);
        Rect cell = _Rect(presented, loot);
        Rect head = _Rect(presented, _Named<TextBlock>(presented, "HeadLoot"));
        Rect perHour = _Rect(presented, _Child<TextBlock>(row, "StatsRowPerHour"));
        Assert.Equal(64, cell.Width, 0.5);
        Assert.True(Math.Abs(cell.X - head.X) < 1.5, $"LOOT cell {cell} and head {head} are not one column at {width}");
        Assert.True(perHour.Right <= cell.X + 0.5, $"ISK/H {perHour} runs into LOOT {cell}");
        Assert.True(cell.Right <= presented.Content.Bounds.Width + 0.5, $"LOOT {cell} leaves the window at {width}");
        Rect name = _Rect(presented, _Child<TextBlock>(row, "StatsRowName"));
        Assert.True(name.Width >= 100, $"the activity name {name} is squeezed at {width}");

        loot.Command?.Execute(null);

        (ActivityStatsKey? key, _) = Assert.Single(opener.Opened);
        Assert.Equal(Sanctum, key);
        Assert.All(_Rows(presented)[0].GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)),
            text => Assert.NotEqual(DimInk, _Color(text.Foreground)));
    }

    /// <summary>TOP DROPS in the pane: the biggest regular drops, a line for the rare ones and a button to the whole table.</summary>
    [AvaloniaFact]
    public async Task TopDrops_ShowTheBiggestDrops_AndTheButtonOpensTheTable()
    {
        var opener = new LootTableViewModelTests.RecordingOpener();
        using var instance = TestClientInstance.Create(_ServicesWithLoot(opener));
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);

        presented.ViewModel.ByActivity.Select(presented.ViewModel.ByActivity.Items[0]);
        await LootTableViewModelTests.WaitAsync(() => presented.ViewModel.ByActivity.Detail.TopDrops.Count > 0);
        _Settle(presented);

        Border topDrops = _Named<Border>(presented, "StatsTopDrops");
        Assert.True(topDrops.IsVisible);
        Assert.Equal(2, presented.ViewModel.ByActivity.Detail.TopDrops.Count);
        Assert.Equal("+ 1 rare kind — 89 % of loot value", _Named<TextBlock>(presented, "StatsTopDropsRare").Text);
        Rect box = _Rect(presented, topDrops);
        Button open = _Named<Button>(presented, "StatsLootTable");
        Assert.True(open.IsVisible);
        Assert.True(box.Contains(_Rect(presented, open)), $"OPEN LOOT TABLE {_Rect(presented, open)} is not inside TOP DROPS {box}");
        Assert.All(topDrops.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)),
            text => Assert.NotEqual(DimInk, _Color(text.Foreground)));

        open.Command?.Execute(null);

        Assert.Equal(Sanctum, Assert.Single(opener.Opened).Key);
    }

    /// <summary>The LOOT TABLES button on the range line opens the window without a site. It is a piece of the range line like
    /// the others: inside the window and overlapping none of them, at every width the line is held to.</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(1100d)]
    [InlineData(758d)]
    [InlineData(630d)]
    [InlineData(420d)]
    public async Task TheLootTablesButton_SitsOnTheRangeLine_ClearOfTheOtherPieces(double width)
    {
        var opener = new LootTableViewModelTests.RecordingOpener();
        using var instance = TestClientInstance.Create(_ServicesWithLoot(opener));
        Presented presented = await _PresentAsync(instance, width);

        Button button = _Named<Button>(presented, "LootTablesButton");
        Assert.True(button.IsVisible);
        Rect rect = _Rect(presented, button);
        double right = presented.Content.Bounds.Width;
        Assert.True(rect.X >= -0.5 && rect.Right <= right + 0.5, $"LOOT TABLES {rect} leaves 0..{right} at {width}");
        foreach (string name in new[] { "RangePrevious", "RangeTitle", "RangeNext", "RangeFigures", "RangeIsk", "SummaryToggle" })
        {
            Rect other = _Rect(presented, _Named<Control>(presented, name));
            if (other.Width > 0 && other.Height > 0)
                Assert.False(rect.Intersects(other), $"LOOT TABLES {rect} overlaps {name} {other} at {width}");
        }

        button.Command?.Execute(null);

        (ActivityStatsKey? key, _) = Assert.Single(opener.Opened);
        Assert.Null(key);
    }

    /// <summary>A run's drawer says how it stands against the typical run, and one click on the line opens the activity's
    /// earnings — BY ACTIVITY, selected. Counter-proof: no line without a typical run.</summary>
    [AvaloniaFact]
    public async Task TheRunsDrawer_ComparesToTypical_AndTheLineOpensTheEarnings()
    {
        using var instance = TestClientInstance.Create(_Services());
        Guid runId = await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, 1303);
        ActivityYieldDto yield = (await instance.Services.GetRequiredService<ICqrsDispatcher>()
            .Query(new GetActivityYieldQuery(runId.ToString()), Token)).Value ?? throw new XunitException("the saved run has no key");
        ActivityStatsKey key = yield.Key;
        presented.Source.Typical = 10_000_000m;
        ActivityOverviewRowViewModel row = presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows).Single();

        presented.ViewModel.Select(row);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Pane.HasComparison);
        _Settle(presented);

        Assert.StartsWith("Compared to typical: -", presented.ViewModel.Pane.ComparisonText);
        Assert.EndsWith("(bottom 25 %)", presented.ViewModel.Pane.ComparisonText);
        Assert.True(_Named<Button>(presented, "PaneComparison").IsVisible);

        _Named<Button>(presented, "PaneComparison").Command?.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Detail.Key == key);
        _Settle(presented);

        Assert.True(presented.ViewModel.IsByActivity);
        Assert.Equal(key, presented.ViewModel.ByActivity.Detail.Key);
        Assert.True(_Named<Panel>(presented, "PaneStats").IsVisible);
    }

    [AvaloniaFact]
    public async Task WithoutATypicalRun_TheRunsDrawerHasNoComparison()
    {
        using var instance = TestClientInstance.Create(_Services());
        await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, 1303);
        presented.Source.Typical = null;

        presented.ViewModel.Select(presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows).Single());
        await Task.Delay(200, Token);
        Dispatcher.UIThread.RunJobs();

        Assert.False(presented.ViewModel.Pane.HasComparison);
        Assert.False(_Named<Button>(presented, "PaneComparison").IsVisible);
    }

    /// <summary>ET-520: the drawer holds the run window's panel over this run — the diagram with the run marked, the rank,
    /// the notable drop of an outlier — and everything that was in it before. Layout claims are Bounds: the block fills the
    /// pane's width, its bar sits inside the padding, nothing runs past the edge. Counter-proof: leave the block out and the
    /// section is not visible; the old comparison line and the ISK section stay either way.</summary>
    [AvaloniaTheory]
    [InlineData(1303)]
    [InlineData(758)]
    public async Task TheRunsDrawer_HoldsThePositionOfTheRun_BesideEverythingItHadBefore(double width)
    {
        var drop = new ActivityTopDropDto { TypeId = 1, Name = "Gistii A-Type Shield Booster", Quantity = 3, ValueIsk = 15_000m, Rarity = LootRarityClass.Faction };
        RunPosition position = RunPositionTests.Position(20_000m, [.. RunPositionTests.Many(8)], drop: drop);
        using var instance = TestClientInstance.Create(services =>
        {
            _Services()(services);
            services.AddSingleton<IRunPositionSource>(new RunPositionTests.FixedPosition(position));
            services.AddSingleton<ILootTableOpener>(new NoLootTables());
        });
        await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, width);
        ActivityOverviewRowViewModel row = presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows).Single();

        presented.ViewModel.Select(row);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Pane.Position?.Earnings.HasFigures is true);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Pane.HasComparison);
        _Settle(presented);

        Control section = _Named<Control>(presented, "PanePositionSection");
        Control block = _Named<Control>(presented, "PanePosition");
        Control bar = _Named<Control>(presented, "BandBar");
        Assert.True(section.IsEffectivelyVisible);
        Assert.True(block.Bounds.Width is >= 395 and <= 435, $"block is {block.Bounds.Width} wide");
        Assert.InRange(bar.Bounds.Width, block.Bounds.Width - 29, block.Bounds.Width - 27);
        Assert.Equal(30, bar.Bounds.Height);
        foreach (TextBlock text in block.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0))
        {
            Point origin = text.TranslatePoint(new Point(0, 0), block) ?? default;
            Assert.True(origin.X + text.Bounds.Width <= block.Bounds.Width + 1, $"{text.Text} runs past the block");
            Assert.NotEqual(DimInk, _Color(text.Foreground));
        }

        Rect label = _Rect(presented, _Named<TextBlock>(presented, "MarkerText"));
        Rect worst = _Rect(presented, _Named<TextBlock>(presented, "WorstLabel"));
        Rect best = _Rect(presented, _Named<TextBlock>(presented, "BestLabel"));
        Rect scale = _Rect(presented, block.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "log scale"));
        Rect barRect = _Rect(presented, bar);
        Assert.True(label.Bottom <= barRect.Top + 0.5 && barRect.Bottom <= worst.Top + 0.5, $"label {label}, bar {barRect}, worst {worst}");
        Assert.False(label.Intersects(worst) || label.Intersects(best));
        Assert.True(worst.Right <= scale.Left + 0.5 && scale.Right <= best.Left + 0.5, $"worst {worst}, log scale {scale}, best {best} overlap at {width}");
        Assert.True(label.Left >= barRect.Left - 0.5 && label.Right <= barRect.Right + 0.5, $"label {label} is off the bar {barRect}");
        Assert.True(block.Bounds.Height < 300, $"the block is {block.Bounds.Height} high; it was 387 before ET-524");
        Assert.Contains("better than", _Named<TextBlock>(presented, "MarkerText").Text);
        Assert.True(_Named<TextBlock>(presented, "MarkerText").IsEffectivelyVisible);
        Assert.True(_Named<TextBlock>(presented, "PositionOutlier").IsEffectivelyVisible);
        Assert.True(_Named<Control>(presented, "PositionTopDrop").IsEffectivelyVisible);
        Assert.True(_Named<Button>(presented, "PositionLootTable").IsEffectivelyVisible);
        Assert.True(_Named<Button>(presented, "PaneComparison").IsEffectivelyVisible);
        Assert.True(_Named<TextBlock>(presented, "PaneIskEmpty").IsEffectivelyVisible);
        Assert.True(section.TranslatePoint(new Point(0, 0), presented.Content)!.Value.Y
                    < _Named<TextBlock>(presented, "PaneIskEmpty").TranslatePoint(new Point(0, 0), presented.Content)!.Value.Y);
    }

    /// <summary>Opening a run's drawer waits for nothing the block needs: the pane is up and the block says it is looking
    /// before the figures are in.</summary>
    [AvaloniaFact]
    public async Task TheRunsDrawer_OpensBeforeThePositionIsRead()
    {
        var pending = new TaskCompletionSource<RunPosition?>();
        using var instance = TestClientInstance.Create(services =>
        {
            _Services()(services);
            services.AddSingleton<IRunPositionSource>(new PendingPosition(pending.Task));
        });
        await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, 1303);

        presented.ViewModel.Select(presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows).Single());
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Pane.Position?.Earnings.IsLoading is true);
        _Settle(presented);

        Assert.True(presented.ViewModel.Pane.HasActivity);
        Assert.True(_Named<TextBlock>(presented, "PaneIskEmpty").IsEffectivelyVisible);
        Assert.True(_Named<TextBlock>(presented, "PositionLoading").IsEffectivelyVisible);
        pending.SetResult(null);
    }

    /// <summary>One of the pilot's own runs in the best or worst list opens: back to RUNS, that run selected.</summary>
    [AvaloniaFact]
    public async Task AnOwnRunInTheBestRuns_OpensThatRunInRuns()
    {
        using var instance = TestClientInstance.Create(_Services());
        Guid runId = await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, 1303);
        presented.Source.OwnRunRef = runId.ToString();
        presented.Source.OwnRunAt = Evening.AddMinutes(15);

        await presented.ViewModel.ShowActivityStatsAsync(Sanctum);
        ActivityRunLineViewModel line = presented.ViewModel.ByActivity.Detail.BestRuns.First(candidate => candidate.IsOwn);
        await line.OpenCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.SelectedRow is not null);

        Assert.True(presented.ViewModel.IsRunsList);
        Assert.Equal(runId, presented.ViewModel.SelectedRow?.RunId);
    }

    /// <summary>A server's tab reads that server and shows ALL PILOTS only: no YOURS column in the head. Counter-proof: ask for
    /// Local on every tab and the request carries no address.</summary>
    [AvaloniaFact]
    public async Task AServerTab_ReadsThatServer_AndHasNoYoursColumn()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        Assert.True(_Named<TextBlock>(presented, "HeadYours").IsVisible);

        var tab = new RunsTabViewModel("Krah Server", "https://krah.invalid", _ => Task.CompletedTask);
        presented.ViewModel.Tabs.Add(tab);
        presented.ViewModel.SelectedTab = tab;
        await ActivityWindowHarness.WaitUntil(() => presented.Source.Requests.Any(request => request.ServerAddress == "https://krah.invalid"));
        await ActivityWindowHarness.WaitUntil(() => !presented.ViewModel.ByActivity.ShowsYoursColumn);
        _Settle(presented);

        Assert.False(_Named<TextBlock>(presented, "HeadYours").IsVisible);
        Assert.Equal("https://krah.invalid", presented.Source.Requests[^1].ServerAddress);
    }

    // ── ET-518: the search box ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The box has a row of its own under the header, inside the content, above the list and clear of the tiles at
    /// every width; empty, it shows no clear button. It is outside the header so the header keeps its height bound (ET-303).</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(758d)]
    [InlineData(420d)]
    public async Task TheSearchBox_SitsUnderTheHeader_InsideTheContent(double width)
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, width);

        Rect box = _Rect(presented, _Named<Control>(presented, "ModuleSearchBox"));
        Assert.True(box.X >= -0.5 && box.Right <= presented.Content.Bounds.Width + 0.5, $"the search box {box} leaves the content at {width}");
        Assert.True(box.Height >= 26, $"the search box is {box.Height} tall");
        Rect characters = _Rect(presented, _Named<Control>(presented, "CharacterFilterBlock"));
        Rect types = _Rect(presented, _Named<Control>(presented, "TypeFilterBlock"));
        Assert.True(box.Y >= Math.Max(characters.Bottom, types.Bottom) - 0.5, $"{box} is not under the tiles {types} {characters} at {width}");
        Assert.True(box.Bottom <= _Rect(presented, _Named<Control>(presented, "ActivityList")).Y + 0.5, $"{box} is not above the list at {width}");
        Assert.False(_Named<Button>(presented, "SearchClear").IsVisible);
    }

    /// <summary>Typing narrows BY ACTIVITY live, "1 of 2" says how much is left, and one click on ✕ brings everything back and
    /// empties the field. Counter-proof: a clear that leaves the term applied keeps one row.</summary>
    [AvaloniaFact]
    public async Task Typing_FiltersByActivityLive_AndOneClearClickBringsItAllBack()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        TextBox box = _Named<TextBox>(presented, "ModuleSearchBox");

        box.Text = "crimson";
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 1);
        _Settle(presented);

        Assert.Single(_Rows(presented));
        Assert.Equal("1 of 2", _Named<TextBlock>(presented, "SearchCount").Text);
        Button clear = _Named<Button>(presented, "SearchClear");
        Assert.True(clear.IsVisible);

        clear.Command?.Execute(null);
        _Settle(presented);

        Assert.Equal(2, _Rows(presented).Count);
        Assert.True(string.IsNullOrEmpty(box.Text));
        Assert.False(clear.IsVisible);
        Assert.Equal(string.Empty, _Named<TextBlock>(presented, "SearchCount").Text);
    }

    /// <summary>Esc in the field clears it, ↓ then ↵ takes a suggestion, and Ctrl+F's target (the well-known name) takes the
    /// focus.</summary>
    [AvaloniaFact]
    public async Task TheKeys_ClearTheField_PickASuggestion_AndCtrlFFindsTheBox()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        TextBox box = _Named<TextBox>(presented, "ModuleSearchBox");

        EveUtils.Client.Input.ShortcutDispatch.FocusSearch(presented.Content);
        Assert.True(box.IsFocused);

        box.Text = "cri";
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Search.Suggestions.Count > 0);
        Assert.True(_Named<Avalonia.Controls.Primitives.Popup>(presented, "SearchSuggestions").IsOpen);
        _Key(box, Key.Down);
        _Key(box, Key.Enter);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 1);
        Assert.Equal("Crimson Cache", box.Text);

        _Key(box, Key.Escape);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 2);
        Assert.True(string.IsNullOrEmpty(box.Text));
    }

    /// <summary>RUNS: a term nothing matches says so with the term and offers clear filters; one click and the run is back.</summary>
    [AvaloniaFact]
    public async Task InRuns_ANoMatch_SaysSo_AndClearFiltersBringsTheRunBack()
    {
        using var instance = TestClientInstance.Create(_Services());
        await _SaveRunAsync(instance, 90000001, Evening);
        Presented presented = await _PresentAsync(instance, 1303);
        TextBox box = _Named<TextBox>(presented, "ModuleSearchBox");

        box.Text = "zzz";
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Search.HasTerm);
        _Settle(presented);

        Assert.Equal("No runs match 'zzz' with the current filters.", _Named<TextBlock>(presented, "TabStatus").Text);
        Button clearFilters = _Named<Button>(presented, "ClearFilters");
        Assert.True(clearFilters.IsVisible);

        clearFilters.Command?.Execute(null);
        _Settle(presented);

        Assert.Single(presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows));
        Assert.False(_Named<Button>(presented, "ClearFilters").IsVisible);
        Assert.True(string.IsNullOrEmpty(box.Text));
    }

    /// <summary>Nothing the search draws is in the dim ink: the clear button, the count and the suggestion lines read in the
    /// text colours.</summary>
    [AvaloniaFact]
    public async Task TheSearch_NeverUsesTheDimInk()
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, 1303);
        await _ShowByActivityAsync(presented);
        _Named<TextBox>(presented, "ModuleSearchBox").Text = "cri";
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 1);
        _Settle(presented);

        Assert.NotEqual(DimInk, _Color(_Named<Button>(presented, "SearchClear").Foreground));
        Assert.NotEqual(DimInk, _Color(_Named<TextBlock>(presented, "SearchCount").Foreground));
        Avalonia.Controls.Primitives.Popup popup = _Named<Avalonia.Controls.Primitives.Popup>(presented, "SearchSuggestions");
        Assert.True(popup.IsOpen);
        foreach (TextBlock text in (popup.Child?.GetVisualDescendants() ?? []).OfType<TextBlock>())
            Assert.NotEqual(DimInk, _Color(text.Foreground));
    }

    private static Color? _Color(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static void _Key(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ET-521: a RUNS row says what its activity typically earns and, when the run is in the best or worst quarter, which.
    /// The row is read once for the whole list, the chip stays inside the row at 758 and 1303, and a run with no typical run behind
    /// it shows neither. Counter-proof: take the chip from the list and the row only has the figure.</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(758d)]
    public async Task ARunsRow_ShowsVsTypicalAndTheQuarterChip_InsideItself(double width)
    {
        Dictionary<string, RunVsTypical> answers = [];
        using var instance = TestClientInstance.Create(services =>
        {
            _Services()(services);
            services.AddSingleton<IRunPositionSource>(new RunPositionTests.FixedPosition(null, answers));
        });
        Guid top = await _SaveRunAsync(instance, 90000001, Evening);
        Guid bottom = await _SaveRunAsync(instance, 90000001, Evening.AddHours(1));
        await _SaveRunAsync(instance, 90000001, Evening.AddHours(2));
        answers[top.ToString()] = new RunVsTypical(38_000_000m, 20_000_000m, RunBand.Top);
        answers[bottom.ToString()] = new RunVsTypical(38_000_000m, -25_000_000m, RunBand.Bottom);
        Presented presented = await _PresentAsync(instance, width);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows).Count(row => row.HasVsTypical) == 2);
        _Settle(presented);

        ActivityOverviewRowViewModel[] rows = [.. presented.ViewModel.Tabs[0].Days.SelectMany(day => day.Rows)];
        Assert.Equal("top 25 %", rows.Single(row => row.RunId == top).StandingText);
        Assert.Equal("bottom 25 %", rows.Single(row => row.RunId == bottom).StandingText);
        Assert.Equal("38M", rows.Single(row => row.RunId == top).VsTypicalText);
        ActivityOverviewRowViewModel without = rows.Single(row => !row.HasVsTypical);
        Assert.False(without.HasStanding);

        List<ActivityRowView> views = [.. _Named<Control>(presented, "ActivityList").GetVisualDescendants().OfType<ActivityRowView>()];
        Assert.Equal(3, views.Count);
        foreach (ActivityRowView view in views)
        {
            var row = Assert.IsType<ActivityOverviewRowViewModel>(view.DataContext);
            Rect rowRect = _Rect(presented, view);
            Border[] chips = [.. view.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("chip") && border.IsEffectivelyVisible
                && border.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == row.StandingText))];
            Assert.Equal(row.HasStanding ? 1 : 0, chips.Length);
            foreach (Border chip in chips)
            {
                Rect rect = _Rect(presented, chip);
                Assert.True(rect.X >= rowRect.X && rect.Right <= rowRect.Right + 0.5 && rect.Y >= rowRect.Y && rect.Bottom <= rowRect.Bottom + 0.5,
                    $"the chip {rect} leaves its row {rowRect} at {width}");
            }

            TextBlock label = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "vs typical");
            Assert.Equal(row.HasVsTypical, label.IsEffectivelyVisible);
            if (row.HasVsTypical)
                Assert.True(_Rect(presented, label).Right <= rowRect.Right - 100, "the figure column leaves the NET column its room");
        }
    }

    /// <summary>ET-521: the search box shows examples while it is empty, and the shortcut as a badge inside it rather than only in
    /// the tooltip; typing swaps the badge for the clear button.</summary>
    [AvaloniaTheory]
    [InlineData(1303d)]
    [InlineData(420d)]
    public async Task TheSearchBox_ShowsExamplesAndTheShortcutBadge_WhileEmpty(double width)
    {
        using var instance = TestClientInstance.Create(_Services());
        Presented presented = await _PresentAsync(instance, width);
        TextBox box = _Named<TextBox>(presented, "ModuleSearchBox");
        Border badge = _Named<Border>(presented, "SearchShortcut");

        Assert.Contains("e.g. Sleeper, Crimson", box.PlaceholderText);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal("Ctrl+F", badge.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Rect boxRect = _Rect(presented, box);
        Rect badgeRect = _Rect(presented, badge);
        Assert.True(badgeRect.X >= boxRect.X && badgeRect.Right <= boxRect.Right + 0.5, $"badge {badgeRect} leaves the box {boxRect}");

        box.Text = "cri";
        _Settle(presented);

        Assert.False(badge.IsEffectivelyVisible);
        Assert.True(_Named<Button>(presented, "SearchClear").IsEffectivelyVisible);
    }

    private static Action<IServiceCollection> _Services() => services => services.AddSingleton<IActivityStatsSource, FakeStatsSource>();

    private static async Task _ShowByActivityAsync(Presented presented)
    {
        _Named<Button>(presented, "ViewByActivity").Command?.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => presented.ViewModel.ByActivity.Items.Count == 2);
        _Settle(presented);
    }

    private static List<Control> _Rows(Presented presented) =>
        [.. _Named<Control>(presented, "StatsList").GetVisualDescendants().OfType<ActivityStatsRowView>().Cast<Control>()];

    private static Rect _Rect(Presented presented, Control control)
    {
        Point origin = control.TranslatePoint(new Point(0, 0), presented.Content) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    /// <summary>The pane and the drawer are two hosts of one view: the one on screen is the one meant.</summary>
    private static T _Named<T>(Presented presented, string name) where T : Control
    {
        List<T> found = [.. presented.Content.GetVisualDescendants().OfType<T>().Where(control => control.Name == name)];
        return found.FirstOrDefault(control => control.IsEffectivelyVisible) ?? found.FirstOrDefault()
               ?? throw new XunitException($"no {typeof(T).Name} named {name}");
    }

    private static T _Child<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new XunitException($"no {typeof(T).Name} named {name} in the row");

    private static void _Settle(Presented presented)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Root.UpdateLayout();
        }
    }

    private static async Task<Guid> _SaveRunAsync(TestClientInstance instance, long characterId, DateTime startedAtUtc)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc,
            2311, "Angel Sanctum", 30000142), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [], [], [], []), Token);
        return started.Value;
    }

    private static async Task<Presented> _PresentAsync(TestClientInstance instance, double width)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Token);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            Crew, runClock: false, paneReadDelay: TimeSpan.Zero, time: RunsTestClock.Fixed);
        await viewModel.LoadAsync(Token);

        var window = new RunsWindow(viewModel) { Width = width, Height = 1000 };
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 1000, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return new Presented(root, content, viewModel, (FakeStatsSource)instance.Services.GetRequiredService<IActivityStatsSource>());
    }

    private sealed class NoLootTables : ILootTableOpener
    {
        public void Open(ActivityStatsKey? key, IReadOnlyList<int> characterIds)
        {
        }
    }

    private sealed class PendingPosition(Task<RunPosition?> position) : IRunPositionSource
    {
        public Task<RunPosition?> ReadAsync(string activityRef, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            position;

        public Task<IReadOnlyDictionary<string, RunVsTypical>> ReadVsTypicalAsync(IReadOnlyCollection<string> activityRefs,
            IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, RunVsTypical>>(new Dictionary<string, RunVsTypical>());
    }

    /// <summary>Two activities, one with plenty of runs from four pilots and one the pilot flew a few times; the best runs of
    /// the first include one of the pilot's own when a reference is set.</summary>
    private sealed class FakeStatsSource : IActivityStatsSource
    {
        private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

        public int Reads { get; private set; }
        public List<ActivityStatsRequest> Requests { get; } = [];
        public decimal? Typical { get; set; } = 10_000_000m;
        public string? OwnRunRef { get; set; }
        public DateTime OwnRunAt { get; set; } = Now;

        public Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default)
        {
            Reads++;
            Requests.Add(request);
            return Task.FromResult(new ActivityStatsOverview([_Stats(Sanctum, 30, 2), _Stats(Cache, 3, 4)]));
        }

        public Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(_Stats(key, Typical is null ? 3 : 30, 2));

        private ActivityStats _Stats(ActivityStatsKey key, int runs, int daysAgo)
        {
            decimal typical = Typical ?? 10_000_000m;
            EarningsSample[] samples = [.. Enumerable.Range(1, runs).Select(index => new EarningsSample
            {
                PilotBucket = $"pilot-{index % 4}", CapWeight = 1, CompletedAtUtc = Now.AddDays(-index), DurationSeconds = 900,
                FixedIsk = typical * (0.2m + index % 9 / 4m), LootBasis = LootBasis.CargoDifference,
                ActivityRef = OwnRunRef is not null && index == 8 ? OwnRunRef : null
            })];
            EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), Now);
            if (OwnRunRef is not null)
                figures = new EarningsFiguresDto
                {
                    Confidence = figures.Confidence, Window = figures.Window, SinceUtc = figures.SinceUtc, RunCount = figures.RunCount,
                    EffectiveRunCount = figures.EffectiveRunCount, PilotCount = figures.PilotCount, ExcludedRunCount = 0,
                    WithoutLootCount = 0, IncompleteLootCount = 0, UnpricedItemCount = 0, Band = figures.Band, Typical = figures.Typical,
                    P10 = figures.P10, P25 = figures.P25, P75 = figures.P75, P90 = figures.P90, TypicalIskPerHour = figures.TypicalIskPerHour,
                    Worst = figures.Worst, Best = figures.Best, WorstRuns = figures.WorstRuns, Yields = figures.Yields,
                    BestRuns =
                    [
                        new EarningsRunDto { CompletedAtUtc = OwnRunAt, YieldIsk = figures.Best?.YieldIsk ?? 1, DurationSeconds = 900, FleetSize = 1, ActivityRef = OwnRunRef },
                        .. figures.BestRuns.Skip(1)
                    ]
                };
            var basis = new ExpectedEarnings(EarningsBasis.AllPilots, figures, 4, "Krah Server", "https://krah.invalid");
            return new ActivityStats(key, key == Sanctum ? "Angel Sanctum" : "Crimson Cache", Now.AddDays(-daysAgo), figures, basis,
                new ExpectedEarnings(EarningsBasis.YourRuns, figures, 1), OwnRunsReason.None, [basis], figures.EffectiveRunCount, Now);
        }
    }
}
