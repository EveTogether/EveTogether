using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Runs;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-519: the loot table window in its states (enough runs, few runs, too few, the site picker), with the rare group
/// open and shut and the columns sorted. Layout claims are Bounds claims: the head and the rows share their columns, nothing
/// leaves the window at its minimum width, and no text is in the dim ink. No picture is taken.
/// </summary>
public sealed class LootTableWindowTests
{
    private static readonly Color DimInk = Color.Parse("#FF8A7E6B");
    private static readonly ActivityStatsKey Sanctum = LootTableViewModelTests.Sanctum;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Presented(ActivityLootWindow Window, LootTableViewModel ViewModel, LootTableViewModelTests.FakeSource Source);

    [AvaloniaTheory]
    [InlineData(760d)]
    [InlineData(640d)]
    public async Task EnoughRuns_TheHeadAndTheRowsShareTheirColumns_AndNothingLeavesTheWindow(double width)
    {
        Presented presented = await _PresentAsync(30, width);

        List<Border> rows = _Rows(presented, "LootItems");
        Assert.Equal(2, rows.Count);
        Assert.True(_Named<Control>(presented, "LootRated").IsVisible);
        Assert.False(_Named<Control>(presented, "LootTooFew").IsVisible);
        Rect head = _Rect(presented, _Named<Control>(presented, "LootHeader"));
        Assert.True(head.Width <= width + 0.5, $"head {head} wider than {width}");
        foreach (Border row in rows)
        {
            Rect rect = _Rect(presented, row);
            Assert.Equal(44, rect.Height, 0.5);
            Assert.True(rect.X >= -0.5 && rect.Right <= width + 0.5, $"row {rect} leaves 0..{width}");
        }

        foreach ((string sort, int column) in new[] { ("SortRate", 2), ("SortQuantity", 3), ("SortValue", 4), ("SortShare", 5) })
        {
            Rect button = _Rect(presented, _Named<Button>(presented, sort));
            Rect cell = _Rect(presented, _Cell(rows[0], column));
            Assert.True(Math.Abs(button.X - cell.X) < 1.5 || Math.Abs(button.Right - cell.Right) < 1.5,
                $"{sort} {button} does not line up with its column {cell} at {width}");
        }

        Rect name = _Rect(presented, _Cell(rows[0], 1));
        Assert.True(name.Width >= 120, $"the item name column {name} is squeezed at {width}");
        Assert.False(_Named<Button>(presented, "LootSinceUpdate").IsVisible);
        _AssertNoDimInk(presented);
    }

    [AvaloniaFact]
    public async Task TheRareGroup_IsOneFoldedLine_UntilItIsOpened()
    {
        Presented presented = await _PresentAsync(30, 760);

        Assert.True(_Named<Control>(presented, "LootRareGroup").IsVisible);
        Assert.Empty(_Rows(presented, "LootRareItems"));
        Rect folded = _Rect(presented, _Named<Control>(presented, "LootRareGroup"));

        _Named<Button>(presented, "LootRareToggle").Command?.Execute(null);
        _Settle(presented);

        Border rare = Assert.Single(_Rows(presented, "LootRareItems"));
        Assert.True(_Rect(presented, rare).Y >= folded.Bottom - 0.5, "the rare rows open under the group line");
        _Named<Button>(presented, "LootRareToggle").Command?.Execute(null);
        _Settle(presented);
        Assert.Empty(_Rows(presented, "LootRareItems"));
        _AssertNoDimInk(presented);
    }

    [AvaloniaFact]
    public async Task AColumnHead_SortsTheRows()
    {
        Presented presented = await _PresentAsync(30, 760);
        string first = presented.ViewModel.Items[0].NameText;

        _Named<Button>(presented, "SortRate").Command?.Execute(null);
        _Settle(presented);

        Assert.NotEqual(first, presented.ViewModel.Items[0].NameText);
        Assert.Contains(" ▼", _Named<Button>(presented, "SortRate").Content?.ToString());
    }

    [AvaloniaFact]
    public async Task FewRuns_ShowTheWarning_AndSeenInXOfN_WithNoPercentages()
    {
        Presented presented = await _PresentAsync(12, 760);

        Assert.True(_Named<Control>(presented, "LootFewRuns").IsVisible);
        Assert.Equal(2, _Rows(presented, "LootItems").Count);
        IEnumerable<string> shown = _Named<Control>(presented, "LootItems").GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsVisible).Select(text => text.Text ?? string.Empty);
        Assert.Contains(shown, text => text.StartsWith("seen in "));
        Assert.DoesNotContain(shown, text => text.EndsWith(" %"));
        _AssertNoDimInk(presented);
    }

    [AvaloniaFact]
    public async Task TooFewRuns_ShowNoTable_JustTheNamesAndHowFarOff()
    {
        Presented presented = await _PresentAsync(3, 760);

        Assert.True(_Named<Control>(presented, "LootTooFew").IsVisible);
        Assert.False(_Named<Control>(presented, "LootRated").IsVisible);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", _Named<TextBlock>(presented, "LootTooFewLine").Text);
        Assert.Equal(3, _Named<Control>(presented, "LootSeenNames").GetVisualDescendants().OfType<TextBlock>().Count(text => text.Classes.Contains("lname")));
        Assert.DoesNotContain(_Named<Control>(presented, "LootTooFew").GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible),
            text => (text.Text ?? string.Empty).Contains('%'));
        _AssertNoDimInk(presented);
    }

    [AvaloniaFact]
    public async Task WithoutASite_TheFlownActivitiesAreListed_ToPickFrom()
    {
        var source = new LootTableViewModelTests.FakeSource();
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(null, new RunRowFacts(null)));
        await viewModel.ShowAsync(null, [1]);
        var window = new ActivityLootWindow(viewModel) { Width = 760, Height = 700 };
        window.Show();
        var presented = new Presented(window, viewModel, source);
        _Settle(presented);

        Assert.True(_Named<Control>(presented, "LootPicking").IsVisible);
        Assert.False(_Named<Control>(presented, "LootTableArea").IsVisible);
        Assert.Equal(2, _Named<Control>(presented, "LootChoices").GetVisualDescendants().OfType<Button>().Count());
        Assert.False(_Named<Button>(presented, "LootSwitchSite").IsVisible);
        _AssertNoDimInk(presented);

        _Named<Control>(presented, "LootChoices").GetVisualDescendants().OfType<Button>().First().Command?.Execute(null);
        await LootTableViewModelTests.WaitAsync(() => viewModel.ShowsTable);
        _Settle(presented);

        Assert.True(_Named<Control>(presented, "LootTableArea").IsVisible);
        Assert.True(_Named<Button>(presented, "LootSwitchSite").IsVisible);
    }

    /// <summary>ET-521: the picker lists every site there is. 300 of them must not become 300 rows: only what is on screen is built,
    /// every row is 44 high inside the window, and a search narrows the list. The unflown ones say "0 of 5 needed".</summary>
    [AvaloniaFact]
    public async Task ThePicker_ShowsEverySite_ButBuildsOnlyTheRowsOnScreen()
    {
        var source = new LootTableViewModelTests.FakeSource
        {
            Flown =
            [
                new(Sanctum, "Angel Sanctum", 12, DateTime.UtcNow.AddDays(-1)),
                .. Enumerable.Range(0, 299).Select(index => new LootSiteChoice(ActivityStatsKey.Site(SiteTypeSource.Site, 3000 + index),
                    $"Site number {index}", 0, null))
            ]
        };
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(null, new RunRowFacts(null)));
        await viewModel.ShowAsync(null, [1]);
        var window = new ActivityLootWindow(viewModel) { Width = 640, Height = 600 };
        window.Show();
        var presented = new Presented(window, viewModel, source);
        _Settle(presented);

        Assert.Equal(300, viewModel.Choices.Count);
        List<Button> realized = [.. _Named<Control>(presented, "LootChoices").GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("lrow"))];
        Assert.InRange(realized.Count, 5, 40);
        foreach (Button row in realized)
        {
            Rect rect = _Rect(presented, row);
            Assert.Equal(44, rect.Height, 0.5);
            Assert.True(rect.X >= -0.5 && rect.Right <= 640.5, $"row {rect} leaves the window");
        }

        Assert.Contains("12 runs with loot", viewModel.Choices[0].MetaText);
        Assert.Contains("0 of 5 needed", viewModel.Choices[1].MetaText);
        _AssertNoDimInk(presented);

        viewModel.SearchText = "number 17";
        _Settle(presented);
        Assert.Equal(11, viewModel.Choices.Count);
    }

    /// <summary>ET-521: the banner that says the game data changed carries a button to the since-update view, which is the
    /// switch above it chosen for the reader; it sits beside the banner, not over it, and goes with the banner once chosen.
    /// Counter-proof: leave the button out and the only way there is the switch at the top.</summary>
    [AvaloniaFact]
    public async Task TheGameDataBanner_OffersSinceUpdateOnly_BesideItself()
    {
        var source = new LootTableViewModelTests.FakeSource { Script = request => new LootTableReading(_UpdatedTable(request.Window), request.Basis, null, OwnRunsReason.None, DateTime.UtcNow) };
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(null, new RunRowFacts(null)), new FakeSdeAccessor());
        await viewModel.ShowAsync(Sanctum, [1]);
        var window = new ActivityLootWindow(viewModel) { Width = 760, Height = 700 };
        window.Show();
        var presented = new Presented(window, viewModel, source);
        _Settle(presented);

        Button show = _Named<Button>(presented, "LootShowSinceUpdateOnly");
        Border banner = _Named<Border>(presented, "LootChanged");
        Assert.True(banner.IsEffectivelyVisible);
        Assert.True(show.IsEffectivelyVisible);
        Assert.Equal("SHOW SINCE UPDATE ONLY", show.Content);
        Assert.True(_Rect(presented, show).X >= _Rect(presented, banner).Right - 0.5, "the button stands beside the banner");
        Assert.True(_Rect(presented, show).Right <= 760.5);
        _AssertNoDimInk(presented);

        show.Command?.Execute(null);
        await LootTableViewModelTests.WaitAsync(() => viewModel.IsSinceUpdate && viewModel.ShowsTable);
        _Settle(presented);

        Assert.Equal(LootTableWindow.SinceUpdate, source.Requests.Last().Window);
        Assert.False(_Named<Border>(presented, "LootChanged").IsEffectivelyVisible);
        Assert.False(show.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheSiteSwitch_IsNamedSwitchSite()
    {
        Presented presented = await _PresentAsync(30, 760);

        Assert.Equal("SWITCH SITE", _Named<Button>(presented, "LootSwitchSite").Content);
    }

    /// <summary>ET-521: Esc closes the window; a search in the picker is cleared first, so the key never throws away what was being
    /// typed with the window. Counter-proof: no handler and the window stays open.</summary>
    [AvaloniaFact]
    public async Task Esc_ClosesTheWindow_AfterClearingASearch()
    {
        var source = new LootTableViewModelTests.FakeSource();
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(null, new RunRowFacts(null)));
        await viewModel.ShowAsync(null, [1]);
        int closed = 0;
        var window = new ActivityLootWindow(viewModel) { Width = 760, Height = 700, CloseRequested = () => closed++ };
        window.Show();
        viewModel.SearchText = "crimson";

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(0, closed);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Assert.Equal(1, closed);

        await viewModel.ShowAsync(Sanctum, [1]);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Assert.Equal(2, closed);
    }

    private static LootTableDto _UpdatedTable(LootTableWindow window)
    {
        DateTime nowUtc = DateTime.UtcNow;
        EarningsSample Run(int daysAgo, bool withTag) => new()
        {
            PilotBucket = $"pilot-{daysAgo}-{withTag}-{Guid.NewGuid():N}", CompletedAtUtc = nowUtc.AddDays(-daysAgo), FixedIsk = 1_000_000m,
            LootBasis = LootBasis.CargoDifference,
            LootGained = [new EarningsLootLine(LootTableViewModelTests.Charge, 5), .. withTag ? [new EarningsLootLine(LootTableViewModelTests.Tag, 3)] : Array.Empty<EarningsLootLine>()]
        };
        EarningsSample[] samples = [.. Enumerable.Range(0, 10).Select(index => Run(30 + index, true)), .. Enumerable.Range(0, 10).Select(index => Run(1 + index, false))];
        return LootTableStatistics.Compute(Sanctum, samples, _ => LootRarityClass.Common, _ => 1_000m, LootTablePolicy.Default, window, nowUtc,
            nowUtc.AddDays(-12));
    }

    /// <summary>The ink of the window — the text colours, the accent of its links, the label grey — over the window
    /// background and over a hovered row, in all four themes: 4.5:1 is the floor.</summary>
    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void TheInk_Reads_InEveryTheme_AlsoOnAHoveredRow(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            Color hover = _Resource("BgRowHoverBrush");
            if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value) || value is not LinearGradientBrush gradient)
                throw new XunitException("WindowBackgroundBrush is not a linear gradient");

            List<string> failures = [];
            foreach ((string inkName, Color ink) in new[]
                     {
                         ("TextBrightBrush", _Resource("TextBrightBrush")), ("TextBrush", _Resource("TextBrush")),
                         ("AccentBrightBrush", _Resource("AccentBrightBrush")), ("label grey", Color.Parse("#FFA0958A"))
                     })
            {
                foreach (GradientStop stop in gradient.GradientStops)
                {
                    foreach ((string surfaceName, Color surface) in new[] { ("window", stop.Color), ("hover", _Composite(hover, stop.Color)) })
                    {
                        double contrast = _Contrast(ink, surface);
                        if (contrast < 4.5)
                            failures.Add($"{inkName} on {surfaceName} over {stop.Color}: {contrast:F2}:1");
                    }
                }
            }

            Assert.True(failures.Count == 0, $"[{faction}] under 4.5:1 — {string.Join("; ", failures)}");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<Presented> _PresentAsync(int runs, double width)
    {
        var source = new LootTableViewModelTests.FakeSource { Runs = runs };
        var viewModel = new LootTableViewModel(source, new ActivityStatsLabels(null, new RunRowFacts(null)));
        await viewModel.ShowAsync(Sanctum, [1]);
        var window = new ActivityLootWindow(viewModel) { Width = width, Height = 700 };
        window.Show();
        var presented = new Presented(window, viewModel, source);
        _Settle(presented);
        return presented;
    }

    private static void _Settle(Presented presented)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Window.UpdateLayout();
        }
    }

    private static T _Named<T>(Presented presented, string name) where T : Control =>
        presented.Window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new XunitException($"no {typeof(T).Name} named {name}");

    private static Rect _Rect(Presented presented, Control control)
    {
        Point origin = control.TranslatePoint(new Point(0, 0), presented.Window) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    private static List<Border> _Rows(Presented presented, string list) =>
        [.. _Named<Control>(presented, list).GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("lrowline"))];

    private static Control _Cell(Border row, int column) =>
        Assert.IsType<Grid>(row.Child).Children.OfType<Control>().First(child => Grid.GetColumn(child) == column);

    private static void _AssertNoDimInk(Presented presented)
    {
        Assert.All(presented.Window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)),
            text => Assert.NotEqual(DimInk, (text.Foreground as ISolidColorBrush)?.Color));
    }

    private static Color _Resource(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new XunitException($"resource {key} is not a solid brush");

    private static Color _Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double _Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static double _Contrast(Color a, Color b)
    {
        double lighter = Math.Max(_Luminance(a), _Luminance(b));
        double darker = Math.Min(_Luminance(a), _Luminance(b));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
