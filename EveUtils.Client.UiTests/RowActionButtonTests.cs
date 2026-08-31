using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Messaging;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Location;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Buttons that share the end of a list row (ET-82). An icon button and a text button take their height from what
/// they happen to contain — a 14px icon against ~12px of 9pt text — so without a shared rule they never line up by
/// themselves, which is what the coupled-servers row showed.
/// </summary>
public class RowActionButtonTests
{
    [AvaloniaFact]
    public async Task ACoupledServerRow_GivesItsGearAndDecoupleButtonsTheSameHeight()
    {
        using var instance = TestClientInstance.Create();
        var owner = new MainWindowViewModel(instance.Services);
        var dialog = new CharacterDialogViewModel(owner,
            new CharacterViewModel(new Character("RaymondKrah", 90250177,
                [LocationScopeCatalog.ReadLocation])));
        await dialog.InitializeAsync();

        dialog.ServerLinks.Add(new ServerLinkViewModel(dialog.CharacterId, "https://eve.local", "eve.local",
            ServerConnectionState.Connected, _ => Task.CompletedTask));

        var window = new CharacterWindow(dialog);
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.DataContext is ServerLinkViewModel)
            .ToList();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.Equal(buttons[0].Bounds.Height, button.Bounds.Height));

        // The height comes from the row's rule rather than from either button's content, so it is above both.
        Assert.True(buttons[0].Bounds.Height >= 24,
            $"the row's buttons should carry the shared height, measured {buttons[0].Bounds.Height}");

        // A shared height only reads as one row while what you can SEE sits in the middle of it — and that has to be
        // measured on the rendered pixels. Neither layout box says it: the ContentPresenter is stretched to fill the
        // button, and so is the label's own box, which then draws its line of glyphs against the top of itself. Both
        // boxes measure perfectly centred while the text visibly is not, which is how the first version of this test
        // passed over the very gap it was written to catch.
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        Assert.All(buttons, button =>
        {
            (int above, int below) = InkGaps(frame!, Area(button, window));
            Assert.True(System.Math.Abs(above - below) <= 1,
                $"'{button.Content}' draws its content with {above}px above it and {below}px below it");
        });

        window.Close();
    }

    /// <summary>Where <paramref name="button"/> sits on the rendered frame.</summary>
    private static PixelRect Area(Visual button, Visual window)
    {
        Point origin = button.TranslatePoint(default, window)
                       ?? throw new InvalidOperationException("the button is not on this window");
        return new PixelRect(
            (int)origin.X, (int)origin.Y, (int)button.Bounds.Width, (int)button.Bounds.Height);
    }

    /// <summary>
    /// Rows of blank pixels above and below whatever <paramref name="area"/> actually draws. The border and its
    /// antialiasing are inset away first, leaving the button's own fill as the blank level to measure against; a row
    /// counts as content once it peaks halfway between that fill and the brightest row in the button.
    /// </summary>
    private static (int Above, int Below) InkGaps(Bitmap frame, PixelRect area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        frame.CopyPixels(area, System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0),
            pixels.Length, area.Width * 4);

        int[] peaks = Enumerable.Range(2, area.Height - 4)
            .Select(y => Enumerable.Range(3, area.Width - 6)
                .Max(x => (pixels[(y * area.Width + x) * 4]
                           + pixels[(y * area.Width + x) * 4 + 1]
                           + pixels[(y * area.Width + x) * 4 + 2]) / 3))
            .ToArray();

        int blank = peaks.Min();
        int brightest = peaks.Max();
        Assert.True(brightest - blank > 20,
            $"nothing legible was drawn in this button — peaks ran {blank}..{brightest}");

        int ink = blank + (brightest - blank) / 2;
        return (Array.FindIndex(peaks, p => p > ink),
                peaks.Length - 1 - Array.FindLastIndex(peaks, p => p > ink));
    }
}
