using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
// The project's implicit usings pull in System.IO, whose Path would otherwise be ambiguous with the shape.
using IconPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The pop-out title bar's three buttons (ET-73). They were two small squares with a rounded, accent-filled PIN pill
/// between them, because the ToggleButton had no style of its own and fell through to the stock theme. They are one
/// set of square icon buttons now, shared by both pop-outs.
///
/// Most of this is a question about how something looks, which a passing assertion cannot answer — the renders these
/// tests save are the point, and the assertions only pin down what must not quietly come undone.
/// </summary>
public class OverlayChromeTests
{
    private sealed class FakeFleet : IFleetOverlaySource
    {
        public string FleetName => "Home Defence";
        public long FleetId => 77;
        public IReadOnlyList<DpsViewModel> Members { get; } = [];
        public FleetCommanderPresence CommanderPresence =>
            FleetCommanderPresence.From("Jita", ["Jita", "Jita", "Perimeter", null]);
    }

    /// <summary>Both pop-outs, because the whole point of the shared chrome is that neither can drift.</summary>
    public enum Popout { PerCharacter, Fleet }

    private static Window Build(Popout which) => which switch
    {
        Popout.PerCharacter => new DpsOverlayWindow(new DpsViewModel("Lionear", isSelf: false)
        {
            Location = "Jita", Bounty = 894_400,
        }) { Width = 420, Height = 240 },
        _ => new FleetOverlayWindow(new FleetOverlayViewModel(new FakeFleet())) { Width = 340, Height = 164 },
    };

    private static (Window Window, Button Opacity, ToggleButton Pin, Button Close) Show(Popout which, bool pinned)
    {
        var window = Build(which);
        window.Topmost = pinned;
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        var row = window.GetVisualDescendants().OfType<OverlayChromeButtons>().Single();
        var buttons = row.GetVisualDescendants().OfType<Button>().ToList();
        var pin = buttons.OfType<ToggleButton>().Single();
        var plain = buttons.Where(b => b is not ToggleButton).ToList();
        return (window, plain[0], pin, plain[1]);
    }

    [AvaloniaTheory]
    [InlineData(Popout.PerCharacter)]
    [InlineData(Popout.Fleet)]
    public void ThreeButtons_SameSquare_SameSize(Popout which)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var (window, opacity, pin, close) = Show(which, pinned: true);

        // The complaint, made checkable: one shape, one size, for all three. The pin used to be a wider rounded pill
        // between two small squares, which is exactly what these three equalities would have caught.
        foreach (var button in new Control[] { opacity, pin, close })
        {
            Assert.Equal(22, button.Bounds.Width);
            Assert.Equal(22, button.Bounds.Height);
            Assert.Equal(button.Bounds.Width, button.Bounds.Height);   // square, not merely equal to each other
            Assert.Equal(new Avalonia.CornerRadius(0), ((TemplatedControl)button).CornerRadius);
        }

        // …and the icons inside them are drawn to one scale. Each sits on the same 16-unit canvas behind the same
        // Viewbox, because letting a Path stretch to its own bounding box instead would draw the two-stroke ✕ at
        // roughly twice the pin's scale — three icons of different weights, which is the complaint all over again.
        var icons = new Control[] { opacity, pin, close }
            .Select(b => b.GetVisualDescendants().OfType<Viewbox>().Single())
            .ToList();
        Assert.All(icons, box => Assert.Equal(icons[0].Bounds.Size, box.Bounds.Size));
        Assert.All(icons, box => Assert.Equal(new Avalonia.Size(16, 16),
            Assert.IsType<Canvas>(box.Child).Bounds.Size));

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Popout.PerCharacter)]
    [InlineData(Popout.Fleet)]
    public void EveryButton_IsAnIcon_WithATooltip(Popout which)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var (window, opacity, pin, close) = Show(which, pinned: true);

        foreach (var button in new Button[] { opacity, pin, close })
        {
            // A drawn shape, not text of any kind — not a word ("PIN", which broke the set), and not a character
            // either. A glyph is whatever the shipped font decides to draw: ⬒ arrived on screen with its bottom half
            // filled and read as a floppy disk, which is why these are geometry now.
            Assert.False(button.Content is string, $"{button.Name ?? "button"} still carries text: {button.Content}");
            var paths = button.GetVisualDescendants().OfType<IconPath>().ToList();
            Assert.NotEmpty(paths);
            Assert.All(paths, path => Assert.NotNull(path.Data));

            // Every icon has to be tinted from the button, or it would sit at the resting colour through hover and,
            // worse, through the pinned state — dark ink is what makes the filled square readable.
            Assert.All(paths, path => Assert.True(path.Stroke is not null || path.Fill is not null));

            // An icon is quick to read once you know it and opaque before, so all three have to say what they are.
            var tip = Assert.IsType<string>(ToolTip.GetTip(button));
            Assert.NotEmpty(tip);
        }

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Popout.PerCharacter)]
    [InlineData(Popout.Fleet)]
    public void ThePin_StillReadsAndWrites_Topmost(Popout which)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        // Only the look was meant to change. The pin is what keeps the overlay over the game, and it is also what
        // the remembered geometry stores — a pin that had quietly stopped writing Topmost would look perfect.
        var (window, _, pin, _) = Show(which, pinned: true);
        Assert.True(pin.IsChecked);

        pin.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Topmost);

        window.Topmost = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(pin.IsChecked);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Popout.PerCharacter)]
    [InlineData(Popout.Fleet)]
    public void PinnedAndUnpinned_LookDifferent_WithoutAnyText(Popout which)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        // The word PIN used to carry the on-state. With an icon only the fill can, so the two states have to differ
        // in actual pixels — assert that rather than trusting a style rule to have been applied.
        var (onWindow, _, onPin, _) = Show(which, pinned: true);
        var onInk = Ink(onPin);
        var on = Capture(onWindow, $"chrome-{which}-pinned".ToLowerInvariant());
        onWindow.Close();

        var (offWindow, _, offPin, _) = Show(which, pinned: false);
        var offInk = Ink(offPin);
        var off = Capture(offWindow, $"chrome-{which}-unpinned".ToLowerInvariant());
        offWindow.Close();

        Assert.NotEqual(on, off);

        // And the tack itself has to invert with the square. Pinned fills the button with the faction accent; an
        // icon that stayed accent-coloured would be an accent tack on an accent square — all but invisible, in the
        // one state this button exists to report. The frame comparison above cannot see this on its own: the
        // background changes either way, so the pixels differ whether or not the ink followed.
        Assert.NotEqual(onInk, offInk);
    }

    [AvaloniaFact]
    public void SmallEnoughToSitBesideTheGame_TheButtonsStillFit()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        // The window's minimum. Chrome that crowds out the readout at the size the operator actually uses is chrome
        // that weighs more than the information under it.
        var window = new FleetOverlayWindow(new FleetOverlayViewModel(new FakeFleet())) { Width = 250, Height = 140 };
        window.Topmost = true;
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        var row = window.GetVisualDescendants().OfType<OverlayChromeButtons>().Single();
        Assert.Equal(3, row.GetVisualDescendants().OfType<Button>().Count());
        Assert.True(row.Bounds.Width <= 80, $"the three buttons take {row.Bounds.Width}px of a 250px title bar");

        Capture(window, "chrome-small");
        window.Close();
    }

    /// <summary>The colour a button's icon is actually drawn in — the Path's own brush, not the button's, so a
    /// binding that had come undone reads as "unchanged" here rather than passing on the button's intent.</summary>
    private static string Ink(Button button) =>
        button.GetVisualDescendants().OfType<IconPath>()
            .Select(path => (path.Stroke ?? path.Fill)?.ToString() ?? "none")
            .First();

    private static string Capture(Window window, string name) => OverlayShots.Capture(window, name);
}
