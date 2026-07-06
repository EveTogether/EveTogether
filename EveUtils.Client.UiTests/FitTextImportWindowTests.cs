using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>Renders the paste-a-fit dialog headless for a visual check of the layout.</summary>
public class FitTextImportWindowTests
{
    [AvaloniaFact]
    public void FitTextImport_Renders()
    {
        var window = new FitTextImportWindow();
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-text-import.png");
    }
}
