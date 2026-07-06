using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.LocalApi;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Settings dialog shows the opt-in LOCAL API SERVER section (enable toggle + port + Start/Stop button + live
/// status dot/label). Renders headless so the running-state indicator is visually verifiable (Iron Law #9 GUI-verify).
/// </summary>
public class SettingsWindowLocalApiTests
{
    [AvaloniaFact]
    public void Settings_ShowsLocalApiSection_RunningState()
    {
        var window = new SettingsWindow(
            currentDirectory: "/home/raymond/.eve/logs",
            detectedDefault: "/home/raymond/.eve/logs",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "build 3374020 (released 2026-06-03)",
            enableLocalApi: true, localApiPort: 8001, localApiStatusLabel: "",
            localApiServer: new StubLocalApi(new LocalApiStatusSnapshot(
                LocalApiStatus.Running, 8001, "http://127.0.0.1:8001")))
        {
            Width = 580,
            MaxHeight = 1100   // lift the scroll clip so the LOCAL API SERVER section is fully in view for the capture
        };

        window.Show();
        // Local API now lives under the "Integrations" category (index 3) — select it so the capture shows that section.
        window.FindControl<Avalonia.Controls.ListBox>("CategoryNav")!.SelectedIndex = 3;
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-settings-localapi.png");
    }

    private sealed class StubLocalApi(LocalApiStatusSnapshot status) : ILocalApiServer
    {
        public LocalApiStatusSnapshot Status { get; } = status;
        public event Action<LocalApiStatusSnapshot>? StatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyAsync(bool enabled, int port, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
