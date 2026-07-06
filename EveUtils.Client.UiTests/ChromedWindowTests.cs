using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pop-out feature windows use the borderless EVE chrome: a code-built template with our own titlebar
/// (hex + title + minimize/close) instead of the OS frame. Asserts the chrome template is applied and renders.
/// </summary>
public class ChromedWindowTests
{
    [AvaloniaFact]
    public void ChromedWindow_AppliesBorderlessChrome()
    {
        var window = new ChromedWindow { Content = new TextBlock { Text = "module body" }, Width = 420, Height = 300, Title = "Module" };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The custom titlebar's window-control buttons are present → our chrome template won over the OS/Window theme.
        var winButtons = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("winbtn"));
        Assert.True(winButtons >= 2, $"expected the chrome min/close buttons, found {winButtons}");

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(Path.GetTempPath(), "eveutils-chromed-window.png"));
        window.Close();
    }
}
