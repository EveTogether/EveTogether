using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Settings dialog shows the currently-loaded SDE build plus a "Re-download &amp; re-import" button (fallback/debug).
/// Renders the dialog headless so the SDE section is visually verifiable.
/// </summary>
public class SettingsWindowSdeTests
{
    [AvaloniaFact]
    public void Settings_ShowsSdeVersion_AndReimportButton()
    {
        var window = new SettingsWindow(
            currentDirectory: "/home/raymond/.eve/logs",
            detectedDefault: "/home/raymond/.eve/logs",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "build 3374020 (released 2026-06-03)")
        {
            Width = 580
        };

        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-settings-sde.png");   // also shows the new DATA FOLDER section + "Show Data Folder"
    }
}
