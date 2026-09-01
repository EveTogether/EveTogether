using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fit browser's card density (ET-110): a grid of cards over the same rows the table shows, each carrying the
/// hull render with the fit's name over it, the three Dogma figures and one popover with everything fitted. What is
/// pinned down here is what the design leans on and what would fail silently — the figures arriving off the UI
/// thread and only for the page on screen, the card staying whole with CCP images switched off, the popover holding
/// no empty rack, and the grid dividing its width on all three presentation paths.
/// </summary>
public class FitCardTests
{
    private const double CardMinWidth = 250;   // FitCardMinWidth in FitBrowserWindow.axaml

    private static EsiFitting Fit(string name, int shipTypeId, params (int TypeId, string Flag, int Qty)[] items) =>
        new(0, name, "", shipTypeId, items.Select(i => new EsiFittingItem(i.TypeId, i.Flag, i.Qty)).ToList());

    /// <summary>A fit with something in every rack the popover knows about.</summary>
    private static EsiFitting FullFit(string name = "Everything") => Fit(name, 627,
        (2, "HiSlot0", 1), (2, "HiSlot1", 1),
        (3, "MedSlot0", 1),
        (4, "LoSlot0", 1),
        (5, "RigSlot0", 1),
        (8, "DroneBay", 5),
        (9, "Cargo", 400));

    /// <summary>A fit with modules only — no rigs, no drones, no cargo.</summary>
    private static EsiFitting ModulesOnlyFit(string name = "Bare") => Fit(name, 627,
        (2, "HiSlot0", 1), (3, "MedSlot0", 1), (4, "LoSlot0", 1));

    private sealed class StubNames : ISdeNameResolver
    {
        public string TypeName(int typeId) => $"Module {typeId}";
        public string? GroupName(int typeId) => typeId == 627 ? "Cruiser" : null;
    }

    /// <summary>Records what the card asked the image server for, and on which thread the engine was run.</summary>
    private sealed class RecordingImages(bool enabled) : ITypeImageProvider
    {
        public readonly List<(int TypeId, TypeImageKind Kind, int Size)> Requested = [];

        public Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(enabled);

        public Task<Bitmap?> GetImageAsync(int typeId, TypeImageKind kind, int size, CancellationToken cancellationToken = default)
        {
            Requested.Add((typeId, kind, size));
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private sealed class StubStats(FitStats? stats = null) : IFitStatsProvider
    {
        public int Calls;
        public int RanOnThread;
        public readonly List<string> Fits = [];

        public Task<FitStats?> ComputeAsync(EsiFitting fit, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            lock (Fits) Fits.Add(fit.Name);
            RanOnThread = Environment.CurrentManagedThreadId;
            return Task.FromResult<FitStats?>(stats ?? Stats(dps: 412.4, ehp: 38_412, speed: 1234.6));
        }

        public Task<FitStats?> ComputeAsync(EsiFitting fit, IReadOnlyList<ModuleInput> modules,
            int? tacticalModeTypeId = null, IReadOnlyList<DroneInput>? activeDrones = null,
            IReadOnlyList<ImplantInput>? boosters = null, SkillSource? skills = null,
            DamageProfile? profile = null, WeatherInput? weather = null,
            IReadOnlyList<FighterInput>? activeFighters = null, CancellationToken cancellationToken = default) =>
            ComputeAsync(fit, cancellationToken);
    }

    private static FitStats Stats(double dps, double ehp, double speed)
    {
        var layer = new ResistLayer(0, 0, 0, 0);
        return new FitStats(
            TotalDps: dps, WeaponDps: dps, DroneDps: 0,
            CpuUsed: 0, CpuOutput: 0, PowerUsed: 0, PowerOutput: 0,
            DroneBayUsed: 0, DroneBayAvailable: 0, DroneBandwidthUsed: 0, DroneBandwidthAvailable: 0,
            CalibrationUsed: 0, CalibrationAvailable: 0,
            Ehp: ehp, ShieldEhp: 0, ArmorEhp: 0, StructureEhp: 0,
            ShieldResists: layer, ArmorResists: layer, StructureResists: layer,
            CapacitorStable: true, CapacitorStablePercent: 0, CapacitorDepletesInSeconds: 0,
            CapacitorCapacity: 0, CapacitorDelta: 0, CapacitorRecharge: 0,
            TargetingRange: 0, ScanResolution: 0, MaxLockedTargets: 0, SensorStrength: 0,
            MaxVelocity: speed, Mass: 0, Agility: 0, AlignTime: 0, WarpSpeed: 0, SignatureRadius: 0,
            ActiveDroneCount: 0, MiningYield: 0, ModuleContributions: []);
    }

    // ── the figures ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Until the engine answers, a card reads dashes rather than zeroes: a fit whose DPS is genuinely 0
    /// (a hauler) and a fit that has not been computed yet must not look the same.</summary>
    [Fact]
    public async Task Figures_ReadAsDashes_UntilTheEngineAnswers()
    {
        var row = new FitRowViewModel(ModulesOnlyFit(), "Tester", new StubNames(), stats: new StubStats());

        Assert.False(row.HasStats);
        Assert.Equal("— dps", row.DpsLabel);
        Assert.Equal("— ehp", row.EhpLabel);
        Assert.Equal("— m/s", row.SpeedLabel);

        await row.LoadStatsAsync();

        Assert.True(row.HasStats);
        Assert.Equal("412 dps", row.DpsLabel);
        Assert.Equal("38k ehp", row.EhpLabel);
        Assert.Equal("1235 m/s", row.SpeedLabel);
    }

    /// <summary>
    /// The figures are computed off the UI thread. Measured on the real engine over the operator's 148 fits, one
    /// fit costs a median 13 ms (p90 22, worst 41) and a page of 25 around 450 ms — run where the cards are drawn
    /// that is a visible stall on every page turn, and nothing about the result would look wrong afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task Figures_AreComputedOffTheUiThread()
    {
        var engine = new StubStats();
        var row = new FitRowViewModel(ModulesOnlyFit(), "Tester", new StubNames(), stats: engine);
        var uiThread = Environment.CurrentManagedThreadId;   // an AvaloniaFact body runs on the UI thread

        await row.LoadStatsAsync();

        Assert.Equal(1, engine.Calls);
        Assert.True(Dispatcher.UIThread.CheckAccess(), "the test itself should be back on the UI thread");
        Assert.NotEqual(uiThread, engine.RanOnThread);
        Assert.True(row.HasStats);   // and the result still lands on the row
    }

    /// <summary>A fit is measured once. Paging back and forth over the same rows must not re-run the engine.</summary>
    [Fact]
    public async Task Figures_AreComputedOncePerFit()
    {
        var engine = new StubStats();
        var row = new FitRowViewModel(ModulesOnlyFit(), "Tester", new StubNames(), stats: engine);

        await row.LoadStatsAsync();
        await row.LoadStatsAsync();
        await row.LoadStatsAsync();

        Assert.Equal(1, engine.Calls);
    }

    /// <summary>EHP is compacted so the figures line holds three readouts at the card's minimum width — a
    /// battleship's raw 92 401 would be the widest thing on it and push the speed off the card.</summary>
    [Theory]
    [InlineData(412d, "412 ehp")]
    [InlineData(3_840d, "3.8k ehp")]
    [InlineData(38_412d, "38k ehp")]
    [InlineData(1_240_000d, "1.2m ehp")]
    public async Task Ehp_IsCompacted_SoTheFiguresLineFits(double ehp, string expected)
    {
        var row = new FitRowViewModel(ModulesOnlyFit(), "Tester", new StubNames(),
            stats: new StubStats(Stats(dps: 0, ehp: ehp, speed: 0)));
        await row.LoadStatsAsync();

        Assert.Equal(expected, row.EhpLabel);
    }

    /// <summary>Only the page on screen is measured. A library of 40 fits costs one page of engine runs, not 40 —
    /// the whole point of doing this per page instead of per library. Turning the page measures the next one, and
    /// coming back measures nothing at all.</summary>
    [Fact]
    public async Task OnlyThePageOnScreen_IsMeasured()
    {
        var engine = new StubStats();
        var rows = Enumerable.Range(1, 40)
            .Select(i => new FitRowViewModel(ModulesOnlyFit($"Fit {i:00}"), "Tester", new StubNames(), stats: engine))
            .ToList();

        var tab = new FitBrowserTabViewModel("Local", rows);   // the default page is 25
        await WaitForAsync(() => tab.PagedRows.All(row => row.HasStats));

        Assert.Equal(25, engine.Calls);
        Assert.All(rows.Skip(25), row => Assert.False(row.HasStats));

        tab.NextPageCommand.Execute(null);
        await WaitForAsync(() => tab.PagedRows.All(row => row.HasStats));
        Assert.Equal(40, engine.Calls);

        tab.PrevPageCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(40, engine.Calls);   // a fit is measured once, however often you page past it
    }

    // ── images off ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>With CCP images switched off the card fetches nothing at all. The provider does not enforce the
    /// setting itself — every caller has to — so an ungated card would go to the network on a fresh install that
    /// had opted out.</summary>
    [Fact]
    public async Task WithImagesOff_TheCardFetchesNothing()
    {
        var images = new RecordingImages(enabled: false);
        var row = new FitRowViewModel(FullFit(), "Tester", new StubNames(), images: images);

        await row.LoadHullRenderAsync();
        await row.LoadHullImageAsync();

        Assert.Empty(images.Requested);
        Assert.False(row.HasHullRender);
        Assert.False(row.HasHullImage);
    }

    /// <summary>The card asks for a render size the CCP image server actually serves. <c>render</c> answers
    /// 32/64/128/256/512/1024 and rejects everything else with HTTP 400 — a size picked by eye rather than checked
    /// would leave every card blank, and nothing else in the app would complain.</summary>
    [Fact]
    public async Task TheCardAsksForARenderSizeTheImageServerServes()
    {
        var images = new RecordingImages(enabled: true);
        var row = new FitRowViewModel(FullFit(), "Tester", new StubNames(), images: images);

        await row.LoadHullRenderAsync();

        var request = Assert.Single(images.Requested);
        Assert.Equal(TypeImageKind.Render, request.Kind);
        Assert.Equal(512, request.Size);
        Assert.Contains(request.Size, new[] { 32, 64, 128, 256, 512, 1024 });
    }

    /// <summary>With no render the header is not an empty picture frame: it carries the hull's class, which differs
    /// per fit, so a page with images off reads as different ships rather than one placeholder repeated.</summary>
    [Fact]
    public void WithoutARender_TheHeaderStillSaysWhatTheHullIs()
    {
        var known = new FitRowViewModel(FullFit(), "Tester", new StubNames());
        Assert.Equal("CRUISER", known.HullWatermark);

        // Before the SDE is imported there is no class — the type label is all there is, and it still beats a blank.
        var unknown = new FitRowViewModel(Fit("Mystery", 999, (1, "HiSlot0", 1)), "Tester", FallbackNameResolver.Instance);
        Assert.False(unknown.HasHullClass);
        Assert.Equal("TYPE 999", unknown.HullWatermark);
    }

    // ── the popover ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One popover for the whole fit, where the table had three column tooltips: every rack the fit
    /// carries, including the rigs, drones and cargo the row never used to expose.</summary>
    [Fact]
    public void Popover_CarriesEveryRackTheFitHas()
    {
        var row = new FitRowViewModel(FullFit(), "Tester", new StubNames());

        Assert.Equal(
            ["HIGH SLOTS", "MID SLOTS", "LOW SLOTS", "RIGS", "DRONE BAY", "CARGO"],
            row.Racks.Select(rack => rack.Header));

        // The header count is the item count, stacked quantities included.
        Assert.Equal(5, row.Racks.Single(r => r.Category is FitSlotCategory.Drone).Count);
        Assert.Equal(400, row.Racks.Single(r => r.Category is FitSlotCategory.Cargo).Count);
        Assert.Equal("×5", row.Racks.Single(r => r.Category is FitSlotCategory.Drone).Lines[0].QuantityLabel);
    }

    /// <summary>A fit without drones or cargo shows no heading for them — an empty "DRONE BAY" is worse than no
    /// drone bay at all.</summary>
    [Fact]
    public void Popover_ShowsNoHeadingForARackTheFitDoesNotHave()
    {
        var row = new FitRowViewModel(ModulesOnlyFit(), "Tester", new StubNames());

        Assert.Equal(["HIGH SLOTS", "MID SLOTS", "LOW SLOTS"], row.Racks.Select(rack => rack.Header));
        Assert.False(row.HasOtherRacks);   // the second column collapses rather than standing empty
        Assert.Equal(3, row.ModuleRacks.Count);
    }

    /// <summary>A cargo hold is not bounded by anything, so the popover caps what it draws and says what it left
    /// out — while the heading keeps the true count.</summary>
    [Fact]
    public void Popover_CapsALongRack_AndSaysWhatItLeftOut()
    {
        var cargo = Enumerable.Range(1, 12).Select(i => (i + 100, "Cargo", 1)).ToArray();
        var row = new FitRowViewModel(Fit("Packed", 627, cargo), "Tester", new StubNames());

        var rack = row.Racks.Single(r => r.Category is FitSlotCategory.Cargo);
        Assert.Equal(12, rack.Lines.Count);
        Assert.Equal(8, rack.VisibleLines.Count);
        Assert.True(rack.HasOverflow);
        Assert.Equal("+4 more", rack.OverflowLabel);
        Assert.Equal(12, rack.Count);   // the heading still tells the truth
    }

    /// <summary>
    /// A rack folds its identical modules onto one line with a count. Six of the same turret is how the fit flies
    /// but not how it reads — listed one per line they were six rows of the same words, and they were what pushed
    /// the popover past the height of the screen it has to sit on. The table's per-rack counts are built from the
    /// ungrouped lists and must not move with it: "6 modules" there still means six modules.
    /// </summary>
    [Fact]
    public void Popover_FoldsIdenticalModulesOntoOneLine_WithoutMovingTheTablesCounts()
    {
        var fit = Fit("Sixgun", 627,
            (2, "HiSlot0", 1), (2, "HiSlot1", 1), (2, "HiSlot2", 1),
            (2, "HiSlot3", 1), (2, "HiSlot4", 1), (7, "HiSlot5", 1),
            (8, "DroneBay", 5), (8, "DroneBay", 3));
        var row = new FitRowViewModel(fit, "Tester", new StubNames());

        var high = row.Racks.Single(rack => rack.Category is FitSlotCategory.High);
        Assert.Equal(2, high.Lines.Count);            // five turrets on one line, the launcher on the next
        Assert.Equal(5, high.Lines[0].Quantity);
        Assert.Equal("×5", high.Lines[0].QuantityLabel);
        Assert.Equal(1, high.Lines[1].Quantity);
        Assert.Equal(6, high.Count);                  // the heading still counts modules, not lines

        // Two drone stacks of the same type read as one line of eight.
        var drones = row.Racks.Single(rack => rack.Category is FitSlotCategory.Drone);
        Assert.Equal(8, Assert.Single(drones.Lines).Quantity);

        // The table's rack column is untouched: it still lists and counts every fitted module separately.
        Assert.Equal(6, row.HighCount);
        Assert.Equal(6, row.HighModules.Count);
        Assert.All(row.HighModules, line => Assert.Equal(1, line.Quantity));
    }

    /// <summary>The popover's icons load on the first hover, not with the card: a page of cards nobody hovers
    /// fetches its hull renders and nothing else.</summary>
    [Fact]
    public async Task Popover_LoadsItsIcons_OnlyWhenTheCardIsHovered()
    {
        var images = new RecordingImages(enabled: true);
        var row = new FitRowViewModel(FullFit(), "Tester", new StubNames(), images: images);

        await row.LoadHullRenderAsync();
        Assert.Single(images.Requested);   // the render, nothing else

        await row.LoadPopoverIconsAsync();

        Assert.Contains(images.Requested, r => r.Kind == TypeImageKind.Icon);
        Assert.Equal(row.Racks.Sum(rack => rack.VisibleLines.Count),
            images.Requested.Count(r => r.Kind == TypeImageKind.Icon));
    }

    // ── the density ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cards are the default, and a chosen density is handed to the caller to remember.</summary>
    [Fact]
    public void Density_DefaultsToCards_AndHandsAChoiceBackToBeRemembered()
    {
        FitBrowserLayout? saved = null;
        var vm = new FitBrowserViewModel([new FitBrowserTabViewModel("Local", new List<FitRowViewModel>())],
            saveLayout: layout => { saved = layout; return Task.CompletedTask; });

        Assert.True(vm.IsCardLayout);
        Assert.False(vm.IsListLayout);

        vm.SetLayoutCommand.Execute(FitBrowserLayout.List);

        Assert.True(vm.IsListLayout);
        Assert.Equal(FitBrowserLayout.List, saved);
    }

    /// <summary>A remembered density that lands after the user has already clicked must not overwrite that click —
    /// the restore is asynchronous and the click is not.</summary>
    [Fact]
    public async Task Density_RestoreDoesNotOverwriteAChoiceThatBeatIt()
    {
        var gate = new TaskCompletionSource<FitBrowserLayout?>();
        var vm = new FitBrowserViewModel([new FitBrowserTabViewModel("Local", new List<FitRowViewModel>())],
            loadLayout: () => gate.Task);

        vm.SetLayoutCommand.Execute(FitBrowserLayout.List);
        gate.SetResult(FitBrowserLayout.Cards);   // the stored value the click replaced
        await Task.Yield();

        Assert.True(vm.IsListLayout);
    }

    // ── the grid, on every presentation path ─────────────────────────────────────────────────────────────────

    public enum Shell { OwnWindow, DockedTab }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private static FitBrowserViewModel BrowserOf(int fits)
    {
        var rows = Enumerable.Range(1, fits)
            .Select(i => new FitRowViewModel(FullFit($"Fit {i:00}"), "Tester", new StubNames()))
            .ToList();
        return new FitBrowserViewModel([new FitBrowserTabViewModel("Local", rows)]);
    }

    private static Control Show(TestClientInstance instance, FitBrowserViewModel vm, Shell shell, double width)
    {
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        var window = new FitBrowserWindow(vm) { Width = width, Height = 660 };

        if (shell is Shell.OwnWindow)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return window;
        }

        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "FITS", "fits", "fits");
        var docked = new Window { Width = width, Height = 660, Content = (Control)Assert.Single(display.HostTabs).Content! };
        docked.Show();
        Dispatcher.UIThread.RunJobs();
        docked.UpdateLayout();
        return docked;
    }

    /// <summary>The card grid divides the width it is given: the last column ends flush with the panel and no
    /// column is narrower than the minimum unless the panel itself is (the narrow docked tab, one column).</summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow, 1100)]
    [InlineData(Shell.OwnWindow, 720)]
    [InlineData(Shell.DockedTab, 900)]
    [InlineData(Shell.DockedTab, 420)]
    public void CardGrid_FillsTheWidth_LeavingNoStripOnTheRight(Shell shell, double width)
    {
        using var instance = TestClientInstance.Create();
        AssertCardsFillTheWidth(Show(instance, BrowserOf(7), shell, width));
    }

    /// <summary>The third presentation path: a grid arranged in a docked tab and then floated has to divide the new
    /// width, not keep the columns it was measured with.</summary>
    [AvaloniaFact]
    public void CardGrid_FillsTheWidth_AfterADockToFloatMigration()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        var window = new FitBrowserWindow(BrowserOf(7)) { Width = 1100, Height = 660 };
        host.Open(window, "FITS", "fits", "fits");

        var docked = new Window { Width = 560, Height = 660, Content = Assert.Single(display.HostTabs).Content };
        docked.Show();
        Dispatcher.UIThread.RunJobs();
        docked.UpdateLayout();
        AssertCardsFillTheWidth(docked);

        // Hand the content back before floating it: SwitchMode reparents the very same DockPanel.
        docked.Content = null;
        docked.Close();
        Dispatcher.UIThread.RunJobs();

        display.IsFloating = true;
        host.SwitchMode();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AssertCardsFillTheWidth(window);
        window.Close();
    }

    /// <summary>One density is on screen at a time. The table is not removed — it is what a column sort and a
    /// price comparison still need — but it must not be laid out underneath the cards.</summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public void OneDensityShowsAtATime(Shell shell)
    {
        using var instance = TestClientInstance.Create();
        var vm = BrowserOf(4);
        var root = Show(instance, vm, shell, 1100);

        Assert.NotNull(CardPanel(root));
        Assert.DoesNotContain(root.GetVisualDescendants().OfType<DataGrid>(), grid => grid.IsVisible);

        vm.SetLayoutCommand.Execute(FitBrowserLayout.List);
        Dispatcher.UIThread.RunJobs();
        ((Control)root).UpdateLayout();

        Assert.Contains(root.GetVisualDescendants().OfType<DataGrid>(), grid => grid.IsVisible);
        Assert.Null(CardPanel(root));
    }

    /// <summary>
    /// The popover never draws outside the width it is given. It used to: the two columns had a fixed width, the
    /// pair wanted more than the ToolTip's <c>MaxWidth</c>, and the overflow was clipped — module names came out cut
    /// mid-word ("Small Emission Scop") with no ellipsis, because each column believed it had room it did not have.
    /// Checked at the width the app-wide tooltip cap would give (the floor, if the scoped override below ever stops
    /// reaching the tooltip) and at the one this screen asks for.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(420)]
    [InlineData(680)]
    public void ThePopover_StaysInsideTheWidthItIsGiven(double cap)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        // Long names in every rack — the shape that overflowed.
        var row = new FitRowViewModel(FullFit(), "Tester", new LongNames());
        var vm = new FitBrowserViewModel([new FitBrowserTabViewModel("Local", [row])]);
        var window = new FitBrowserWindow(vm) { Width = 1100, Height = 660 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("fitcard"));
        var tip = (Control)ToolTip.GetTip(card)!;
        ToolTip.SetTip(card, null);
        ((ISetLogicalParent)tip).SetParent(null);
        tip.DataContext = row;
        window.Close();

        // Headless cannot open a real tooltip popup ("no overlay layer"), so the tip is stood in the constraint the
        // tooltip would impose. Same content, same cap.
        var host = new Border { MaxWidth = cap, Padding = new Thickness(11, 9), Child = tip };
        var stand = new Window { Width = 900, Height = 900, Content = new Panel { Children = { host } } };
        stand.Show();
        Dispatcher.UIThread.RunJobs();
        stand.UpdateLayout();

        Assert.True(host.Bounds.Width <= cap, $"the popover took {host.Bounds.Width} of a {cap} cap");
        foreach (var text in tip.GetVisualDescendants().OfType<TextBlock>())
        {
            var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), host)?.X ?? 0;
            Assert.True(right <= host.Bounds.Width + 0.5,
                $"'{text.Text}' reaches {right:F0} in a popover {host.Bounds.Width:F0} wide — it would be cut, not trimmed");
        }
        stand.Close();
    }

    /// <summary>
    /// The equipment popover is wider than a one-line hint, so this screen raises the app-wide 420 tooltip cap on
    /// its own content root. On the CONTENT and not the window (ET-42): docked, that DockPanel is what gets
    /// reparented, and a style left on the window would be dropped — the popover would silently fall back to the
    /// narrow cap in the docked tab only.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public void ThePopoversWidthOverride_TravelsWithTheContent(Shell shell)
    {
        using var instance = TestClientInstance.Create();
        var root = Show(instance, BrowserOf(2), shell, 1100);

        // The content root that carries this screen's styles — the DockPanel ModuleHostService reparents.
        // The window's own Content, not the first DockPanel in the tree: ChromedWindow's template builds one of its
        // own for the titlebar, and that one is above the content and carries none of this screen's styles.
        var content = root as DockPanel ?? (DockPanel)((ContentControl)root).Content!;
        var probe = new ToolTip();
        content.Children.Add(probe);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(680, probe.MaxWidth);   // 420 is the app-wide value this has to beat
    }

    private sealed class LongNames : ISdeNameResolver
    {
        // The longest names in the operator's own library run to ~58 characters.
        public string TypeName(int typeId) => $"Eifyr and Co. 'Gunslinger' Medium Projectile Turret MP-{typeId:000}";
        public string? GroupName(int typeId) => typeId == 627 ? "Cruiser" : null;
    }

    private static FillGridPanel? CardPanel(Control root) =>
        root.GetVisualDescendants().OfType<FillGridPanel>().FirstOrDefault(panel => panel.IsEffectivelyVisible);

    private static void AssertCardsFillTheWidth(Control root)
    {
        var panel = CardPanel(root);
        Assert.NotNull(panel);

        Rect[] cards = panel!.Children.Select(c => c.Bounds).ToArray();
        Assert.NotEmpty(cards);
        Assert.True(panel.Bounds.Width > 0, "the card panel rendered with no width");
        Assert.Equal(panel.Bounds.Width, cards.Max(c => c.Right), 1);
        Assert.All(cards, c => Assert.True(
            c.Width >= Math.Min(panel.Bounds.Width, CardMinWidth) - 1,
            $"a card fell below the minimum in a panel of {panel.Bounds.Width}: {c.Width}"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the page never finished filling in");
    }
}
