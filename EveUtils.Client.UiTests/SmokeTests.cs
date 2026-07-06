using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Proves the headless harness works on this machine: Skia software-rendering produces real pixels and
/// simulated input reaches controls. If these fail, the deeper UI tests cannot be trusted.
/// </summary>
public class SmokeTests
{
    [AvaloniaFact]
    public void Headless_Skia_Renders_Real_Pixels()
    {
        var window = new Window
        {
            Width = 200,
            Height = 100,
            Content = new Border { Background = Brushes.Red },
        };

        window.Show();
        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal(200, frame!.PixelSize.Width);
        Assert.Equal(100, frame.PixelSize.Height);
        frame.Save("/tmp/eveutils-headless-smoke.png");
    }

    [AvaloniaFact]
    public void Simulated_Click_Reaches_Button()
    {
        var clicks = 0;
        var button = new Button
        {
            Content = "Hit me",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        button.Click += (_, _) => clicks++;
        var window = new Window { Width = 120, Height = 80, Content = button };

        window.Show();
        window.MouseDown(new Point(60, 40), MouseButton.Left);
        window.MouseUp(new Point(60, 40), MouseButton.Left);

        Assert.Equal(1, clicks);
    }
}
