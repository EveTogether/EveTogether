using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fittings.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fit browser's card density (ET-110): a grid of cards over the same rows the table shows, each carrying the
/// hull render with the fit's name and hull over it, who uploaded it, what it is worth, and one popover with
/// everything fitted. What is pinned down here is what the design leans on and what would fail silently — images
/// fetched only for the page on screen, the card staying whole with CCP images switched off, the uploader's avatar
/// falling back rather than showing an empty frame, the popover holding no empty rack and never clipping a name,
/// and the grid dividing its width on all three presentation paths.
/// </summary>
public class FitCardTests
{
    private const double CardMinWidth = 202;   // FitCardMinWidth in FitBrowserWindow.axaml

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

    // ── the uploader's avatar ────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingPortraits : ICharacterPortraitProvider
    {
        public readonly List<(int CharacterId, int Size)> Requested = [];

        public Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default)
        {
            Requested.Add((characterId, size));
            // The real provider answers null for a character id of 0 and when images are off; it never throws.
            return Task.FromResult<Bitmap?>(null);
        }
    }

    /// <summary>
    /// Not every row has a character behind its uploader name, so the avatar is asked for only when there is an id
    /// to ask about. A server-shared fit always names one; a local fit owned by a gamelog-only pilot has no ESI id
    /// (<c>CharacterViewModel.CharacterId</c> is 0 for those) and an imported fit may match no character at all.
    /// Those rows keep their initial rather than firing a request that could only 404.
    /// </summary>
    [Fact]
    public async Task TheAvatar_IsOnlyFetchedForARowThatHasACharacter()
    {
        var portraits = new RecordingPortraits();
        var known = new FitRowViewModel(FullFit(), "Vaelor Kestrane", new StubNames(),
            portraits: portraits, uploaderCharacterId: 2112625428);
        var anonymous = new FitRowViewModel(FullFit(), "Imported", new StubNames(), portraits: portraits);

        await known.LoadUploaderPortraitAsync();
        await anonymous.LoadUploaderPortraitAsync();

        var request = Assert.Single(portraits.Requested);
        Assert.Equal(2112625428, request.CharacterId);
        // portrait serves 32/64/128/256/512 and rejects the rest with HTTP 400 — checked, like the hull render.
        Assert.Equal(64, request.Size);
        Assert.Contains(request.Size, new[] { 32, 64, 128, 256, 512 });
    }

    /// <summary>Every row has something to show in the circle, so a row without a portrait is not an empty frame —
    /// and the name after it starts in the same place on every card.</summary>
    [Theory]
    [InlineData("Vaelor Kestrane", "V")]
    [InlineData("imported", "I")]
    [InlineData("", "?")]
    public void WithoutAPortrait_TheCircleCarriesTheUploadersInitial(string uploader, string expected)
    {
        var row = new FitRowViewModel(FullFit(), uploader, new StubNames());

        Assert.False(row.HasUploaderPortrait);
        Assert.Equal(expected, row.UploaderInitial);
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

    /// <summary>Only the page on screen fetches. A library of 40 fits pulls one page's worth of renders, row icons
    /// and portraits, not forty — the whole point of doing this per page instead of per library.</summary>
    [Fact]
    public void OnlyThePageOnScreen_FetchesItsImages()
    {
        var images = new RecordingImages(enabled: true);
        var portraits = new RecordingPortraits();
        var rows = Enumerable.Range(1, 40)
            .Select(i => new FitRowViewModel(ModulesOnlyFit($"Fit {i:00}"), "Tester", new StubNames(),
                images: images, portraits: portraits, uploaderCharacterId: 100 + i))
            .ToList();

        _ = new FitBrowserTabViewModel("Local", rows);   // the default page is 25

        Assert.Equal(25, portraits.Requested.Count);
        Assert.Equal(25, images.Requested.Count(r => r.Size == FitRowViewModel.RenderSize));
        Assert.Equal(25, images.Requested.Count(r => r.Size == 64));   // the table's row icon
        Assert.All(portraits.Requested, r => Assert.InRange(r.CharacterId, 101, 125));
    }

    /// <summary>
    /// The card trims a long fit name to fit the card; the popover is then the only place left to read it, so it
    /// carries the name in full and wraps rather than trimming again.
    /// </summary>
    [AvaloniaFact]
    public void ThePopover_CarriesTheFitsFullName()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        const string longName = "Superior + Standard Sleeper Cache + Hisec Combat Site Stratios";
        var row = new FitRowViewModel(FullFit(longName), "Tester", new StubNames());
        var vm = new FitBrowserViewModel([new FitBrowserTabViewModel("Local", [row])]);
        var window = new FitBrowserWindow(vm) { Width = 1100, Height = 660 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("fitcard"));

        // On the card the name is trimmed — that is deliberate and stays.
        var onCard = card.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == longName);
        Assert.Equal(TextTrimming.CharacterEllipsis, onCard.TextTrimming);

        // In the popover it is the title, in full, wrapping instead of trimming.
        var tip = (Control)ToolTip.GetTip(card)!;
        ToolTip.SetTip(card, null);
        ((ISetLogicalParent)tip).SetParent(null);
        tip.DataContext = row;

        var title = tip.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == longName);
        Assert.Equal(TextWrapping.Wrap, title.TextWrapping);
        Assert.Equal(TextTrimming.None, title.TextTrimming);
        window.Close();
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

    /// <summary>
    /// The card's left-aligned lines line up as INK, not as layout rectangles. This is the one thing on the card
    /// that needs a test rather than an eye: it regressed once for five pixels — the fit name sat five px right of
    /// the hull name while both layout boxes read exactly 13.00, because the two lived in different containers —
    /// and every layout-level assertion stayed green through it. Checked with a fit and a hull that start with the
    /// same letter, which is what makes a difference unmistakable instead of arguable, and with the uploader's
    /// avatar, which has no side bearing at all and so pins the column absolutely.
    /// </summary>
    [AvaloniaFact]
    public void TheCardsLeftEdgeLinesUp_AsInkAndNotAsLayout()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var row = new FitRowViewModel(FullFit("HACKER PRO"), "Jithran", new FixedHull("Helios"));
        var vm = new FitBrowserViewModel([new FitBrowserTabViewModel("Local", [row])]);
        var window = new FitBrowserWindow(vm) { Width = 1100, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var frame = window.CaptureRenderedFrame()!;
        var pixels = Pixels(frame, out var width);

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("fitcard"));
        var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "HACKER PRO");
        var hull = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Helios");

        var titleInk = FirstInkColumn(pixels, width, title, window);
        var hullInk = FirstInkColumn(pixels, width, hull, window);

        Assert.True(Math.Abs(titleInk - hullInk) <= 1,
            $"the fit name's ink starts at {titleInk} and the hull's at {hullInk} — that gap is what the operator sees");

        // And both sit on the card's own inset, so neither drifted together in the wrong place.
        var cardLeft = (int)(card.TranslatePoint(default, window)?.X ?? 0);
        Assert.InRange(titleInk - cardLeft, 12, 15);
    }

    private sealed class FixedHull(string hull) : ISdeNameResolver
    {
        public string TypeName(int typeId) => hull;
        public string? GroupName(int typeId) => "Covert Ops";
    }

    /// <summary>The captured frame as BGRA bytes.</summary>
    private static byte[] Pixels(WriteableBitmap frame, out int width)
    {
        width = frame.PixelSize.Width;
        var buffer = new byte[width * frame.PixelSize.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, width, frame.PixelSize.Height),
                handle.AddrOfPinnedObject(), buffer.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }

    /// <summary>The first column of the block's own band that is lit well above its background — where the reader's
    /// eye says the line begins.</summary>
    private static int FirstInkColumn(byte[] pixels, int width, TextBlock block, Visual root)
    {
        var at = block.TranslatePoint(default, root)!.Value;
        int x0 = (int)at.X - 4, y0 = (int)at.Y, height = (int)block.Bounds.Height;

        int Level(int x, int y)
        {
            var i = (y * width + x) * 4;
            return Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
        }

        var background = Level(x0 - 2, y0 + height / 2);
        for (var x = x0; x < x0 + 26; x++)
            for (var y = y0; y < y0 + height; y++)
                if (Level(x, y) > background + 60) return x;

        return -1;
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

}
