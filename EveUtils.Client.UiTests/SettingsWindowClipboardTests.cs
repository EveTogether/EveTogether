using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-377: the clipboard-unsupported disclosure in Settings said "Windows only", but the feature also works on
/// Linux under Wayland with wl-clipboard installed. Pins the text shown once a platform source reports itself
/// unsupported (macOS, X11-only Linux, or Wayland without wl-clipboard).
/// </summary>
public class SettingsWindowClipboardTests
{
    // A1: with an unsupported source, ClipboardUnsupportedBlock is visible and names where the feature does work
    // instead of the old, wrong "Windows only".
    [AvaloniaFact]
    public void ClipboardUnsupported_NamesWaylandAndWlClipboard_NotWindowsOnly()
    {
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
            NullLogger<ClipboardWatchService>.Instance, new UnsupportedFakeClipboardChangeSource());

        var window = new SettingsWindow(
            currentDirectory: "", detectedDefault: "",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "", clipboardWatch: watch);
        window.Show();

        var block = window.FindControl<TextBlock>("ClipboardUnsupportedBlock");
        Assert.NotNull(block);
        Assert.True(block.IsVisible);
        Assert.Contains("Wayland", block.Text);
        Assert.Contains("wl-clipboard", block.Text);
        Assert.DoesNotContain("Windows only", block.Text);
    }

    private sealed class UnsupportedFakeClipboardChangeSource : IClipboardChangeSource
    {
        public bool IsSupported => false;

        public event Action? Changed;

        public event Action? SupportChanged;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);

        public void RaiseChanged() => Changed?.Invoke();

        public void RaiseSupportChanged() => SupportChanged?.Invoke();
    }
}
