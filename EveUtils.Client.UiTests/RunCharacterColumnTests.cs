using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-164 — the run window's character column. What is being proved here is that the hex has three fills and not
/// two: a pilot without an ESI link has no character id, so there is no portrait to be had with images on OR off,
/// and until now that state and "the render did not arrive" were the same picture.
/// </summary>
public class RunCharacterColumnTests
{
    /// <summary>The provider's real answer shape: a render for the characters it has one for, null for everyone
    /// else — which is what images-off, offline and a 404 all come back as.</summary>
    private sealed class Portraits(params int[] withRender) : ICharacterPortraitProvider
    {
        public readonly List<(int CharacterId, int Size)> Asked = [];

        public Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default)
        {
            Asked.Add((characterId, size));
            Bitmap? render = withRender.Contains(characterId)
                ? new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul)
                : null;
            return Task.FromResult(render);
        }
    }

    // ── AC-1 — three states, three pictures ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task ThreeCharacters_LinkedWithRender_LinkedWithout_AndUnlinked_RenderThreeDifferentHexes()
    {
        var portraits = new Portraits(900001);
        ActivityWindowViewModel model = _Column(
            new Character("Ravnholt", 900001),      // linked, and the render arrives
            new Character("Kaska Vex", 900002),     // linked, but images are off — the provider answers null
            new Character("Bex Harrow"));           // no ESI link at all: no id to ask with
        foreach (RunCharacterRowViewModel row in model.RunCharacters)
            await row.LoadPortraitAsync(portraits, TestContext.Current.CancellationToken);

        using ShownWindow shown = _Show(model);
        List<string> fills = [.. _Hexes(shown.Window).Select(_FillOf)];

        Assert.Equal(["portrait", "glyph", "hatched"], fills);
        Assert.Equal(3, fills.Distinct().Count());
        Assert.Equal("BH", model.RunCharacters[2].Initials);
    }

    // ── AC-2 — the unlinked pilot is one of the six, not an exception ────────────────────────────────

    [AvaloniaFact]
    public async Task AFleetOfSix_WithOneUnlinkedMember_RendersSixHexes_WithNoGap()
    {
        var portraits = new Portraits(900001, 900002, 900004, 900005, 900006);
        ActivityWindowViewModel model = _Column(
            new Character("Ravnholt", 900001),
            new Character("Kaska Vex", 900002),
            new Character("Bex Harrow"),
            new Character("Deio Tarn", 900004),
            new Character("Nilsa Orn", 900005),
            new Character("Torv Kesh", 900006));
        foreach (RunCharacterRowViewModel row in model.RunCharacters)
            await row.LoadPortraitAsync(portraits, TestContext.Current.CancellationToken);

        using ShownWindow shown = _Show(model);
        List<Panel> hexes = _Hexes(shown.Window);

        Assert.Equal(6, hexes.Count);
        // Every hex carries a fill, so the unlinked member is a row like the rest rather than a hole in the column.
        Assert.DoesNotContain("nothing", hexes.Select(_FillOf));
        Assert.Single(hexes, hex => _FillOf(hex) == "hatched");
        // …and nothing was fetched on their behalf: an unlinked pilot has no id to put a placeholder image under.
        Assert.DoesNotContain(portraits.Asked, asked => asked.CharacterId <= 0);
    }

    // ── AC-3 — the column costs no download of its own ───────────────────────────────────────────────

    [AvaloniaFact]
    public async Task EveryPortrait_IsAskedAtSize128_TheSameCacheKeyAsTheMainWindowsColumn()
    {
        var portraits = new Portraits(ActivityWindowHarness.CharacterId);
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<ICharacterPortraitProvider>(portraits));

        await harness.OpenAsync();

        // 128 is what MainWindowViewModel.LoadCharacterPortraitsAsync asks for; any other size is a second file
        // {id}_{size}.png under character-portraits and so a second download per character.
        Assert.All(portraits.Asked, asked => Assert.Equal(128, asked.Size));
        Assert.Single(portraits.Asked.Select(asked => asked.CharacterId).Distinct());
    }

    // ── AC-5 — the ring and the dot carry the signal together ───────────────────────────────────────

    [AvaloniaFact]
    public void ARestlessCharacter_CarriesBothTheRingColourAndTheDot_AndACalmOneCarriesNeither()
    {
        ActivityWindowViewModel model = _Column(
            new Character("Deio Tarn", 900004),
            new Character("Nilsa Orn", 900005),
            new Character("Ravnholt", 900001));

        using ShownWindow shown = _Show(model);
        model.RunCharacters[0].Attention = RunCharacterAttention.Critical;
        model.RunCharacters[1].Attention = RunCharacterAttention.Warning;
        Dispatcher.UIThread.RunJobs();

        List<Panel> hexes = _Hexes(shown.Window);
        Color red = _Colour(shown.Window, "RedBrush");
        Color amber = _Colour(shown.Window, "ValueBrush");

        Assert.True(_IsFilledWith(_Ring(hexes[0]), red));
        Assert.True(_Dot(hexes[0]).IsEffectivelyVisible);
        Assert.True(_IsFilledWith(_Dot(hexes[0]), red));

        Assert.True(_IsFilledWith(_Ring(hexes[1]), amber));
        Assert.True(_Dot(hexes[1]).IsEffectivelyVisible);
        Assert.True(_IsFilledWith(_Dot(hexes[1]), amber));

        // A character asking nothing wears nothing: neither half of the signal, so the two above stay findable.
        Assert.False(_Dot(hexes[2]).IsEffectivelyVisible);
        Assert.False(_IsFilledWith(_Ring(hexes[2]), red));
        Assert.False(_IsFilledWith(_Ring(hexes[2]), amber));
    }

    // ── AC-6 — a resting toon stays in the column, dimmed whole ─────────────────────────────────────

    [AvaloniaFact]
    public void AToonWithoutARunningRun_StaysInTheColumn_DimmedAsAWhole_AndCanStillBeStarted()
    {
        ActivityWindowViewModel model = _Column(
            new Character("Ravnholt", 900001),
            new Character("Deio Tarn", 900004),
            new Character("Torv Kesh", 900006));

        using ShownWindow shown = _Show(model);
        model.RunCharacters[0].HasRunningRun = true;
        model.RunCharacters[1].HasRunningRun = true;
        model.RunCharacters[1].Attention = RunCharacterAttention.Critical;
        Dispatcher.UIThread.RunJobs();

        List<Panel> hexes = _Hexes(shown.Window);

        Assert.Equal(3, hexes.Count);
        // Ring and fill dim together — the opacity sits on the hex they share, not on one of them.
        Assert.True(hexes[2].Opacity < 1);
        Assert.Equal(1d, _Ring(hexes[2]).Opacity);
        Assert.Equal(1d, hexes[0].Opacity);

        // Dimmed, but not a signal: only the one restless character is marked.
        Assert.Single(hexes, hex => _Dot(hex).IsEffectivelyVisible);

        // The START entry: with no run on the clock, clicking the resting toon puts the window on it, and START
        // is then the button that files the run under that character.
        Assert.True(model.SelectRunCharacterCommand.CanExecute(model.RunCharacters[2]));
        model.SelectRunCharacterCommand.Execute(model.RunCharacters[2]);
        Assert.True(model.RunCharacters[2].IsSelected);
        Assert.True(model.IsStartButtonVisible);
    }

    // ── AC-7 — n characters, and n = 1 is one of them ───────────────────────────────────────────────

    [AvaloniaFact]
    public async Task WithASingleCharacter_TheColumnStandsAsOneRow_RatherThanHidingItself()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        using ShownWindow shown = _Show(model);
        Border column = shown.Window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("charcolumn"));

        Assert.True(model.HasRunCharacters);
        Assert.True(column.IsEffectivelyVisible);
        Assert.Single(_Hexes(shown.Window));
        Assert.Equal(ActivityWindowHarness.CharacterName, model.RunCharacters[0].Name);
    }

    // ── Reading what is on screen ───────────────────────────────────────────────────────────────────

    private static ActivityWindowViewModel _Column(params Character[] characters)
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, new ServiceCollection().BuildServiceProvider());
        foreach (Character character in characters)
            model.RunCharacters.Add(new RunCharacterRowViewModel(character));
        return model;
    }

    /// <summary>Opening the window loads it and starts its clock, and that first tick re-derives every row from
    /// the window's own state — so a row state under test is set after this, never before it.</summary>
    private static ShownWindow _Show(ActivityWindowViewModel model)
    {
        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new ShownWindow(window);
    }

    private static List<Panel> _Hexes(Window window) =>
        [.. window.GetVisualDescendants().OfType<Panel>().Where(panel => panel.Classes.Contains("charhexart"))];

    /// <summary>Which of the three fills the operator is actually looking at in this hex.</summary>
    private static string _FillOf(Panel hex)
    {
        if (hex.GetVisualDescendants().OfType<Image>().Any(image => image.IsEffectivelyVisible))
            return "portrait";

        return hex.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && block.Classes.Contains("charinitials"))
            .Select(block => block.Classes.Contains("unlinked") ? "hatched" : "glyph")
            .FirstOrDefault() ?? "nothing";
    }

    private static Path _Ring(Panel hex) =>
        hex.GetVisualDescendants().OfType<Path>().First(path => path.Classes.Contains("charring"));

    private static Ellipse _Dot(Panel hex) =>
        hex.GetVisualDescendants().OfType<Ellipse>().First(dot => dot.Classes.Contains("chardot"));

    /// <summary>The resting ring is a faction gradient, so "is it this signal colour" is the question — not
    /// "which flat colour is it", which cannot be asked of a gradient at all.</summary>
    private static bool _IsFilledWith(Shape shape, Color colour) =>
        shape.Fill is ISolidColorBrush brush && brush.Color == colour;

    private static Color _Colour(Window window, string key) =>
        window.TryFindResource(key, out object? found) && found is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"{key} is not a solid-colour brush in this window");

    private sealed class ShownWindow(Window window) : IDisposable
    {
        public Window Window { get; } = window;

        public void Dispose() => Window.Close();
    }
}
