using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>Renders the export-a-fit dialog headless for a visual check of the EFT + DNA layout.</summary>
public class FitExportWindowTests
{
    [AvaloniaFact]
    public void FitExport_Renders()
    {
        var eft = "[Rifter, Test Rifter]\nDamage Control II\n\n1MN Afterburner II\n\n200mm AutoCannon II, EMP S";
        var window = new FitExportWindow(
            "Test Rifter", eft, "587:2048;1:438;1:2889;1:12608;1::", "https://eveship.fit/?fit=v3:H4sIAAAAAAAAAyvOyCzQ…");
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-export.png");
    }
}
