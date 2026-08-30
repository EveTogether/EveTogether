using Avalonia.Input;
using Avalonia.Interactivity;
using EveUtils.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>
/// Borderless, pinnable, opacity-adjustable per-character DPS overlay. The chrome — drag, edge resize, opacity, pin
/// and remembering all of it — is <see cref="OverlayWindow"/>, shared with the fleet overlay; this adds the one
/// character's readout and the <c>DpsGraph</c> control (line smoothing on).
/// </summary>
public partial class DpsOverlayWindow : OverlayWindow
{
    private readonly string _character = string.Empty;

    protected override string GeometryKey =>
        string.IsNullOrWhiteSpace(_character) ? string.Empty : OverlayGeometryStore.ForCharacter(_character);

    public DpsOverlayWindow()
    {
        InitializeComponent();
        UseBackdrop(Backdrop);
    }

    public DpsOverlayWindow(DpsViewModel tracker) : this()
    {
        DataContext = tracker;
        _character = tracker.Character;
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Click the graph to bring this character's EVE client to the front (eve-o-preview style). The probe matches by
    // window title, so it only works while that client is logged in as this character; otherwise it's a no-op.
    private void OnActivateClient(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrEmpty(_character))
            return;
        Program.Services?.GetService<Platform.IEveClientProbe>()?.Activate(_character);
    }
}
