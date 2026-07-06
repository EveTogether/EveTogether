using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// On-start ESI-invite prompt — a pure UI seam. When a fleet is started while members lack an ESI link,
/// this offers an "invite via ESI" checkbox; the checkbox does nothing (no ESI call is made yet). Returns true if
/// the owner pressed Start (proceed), false on cancel.
/// </summary>
public partial class StartFleetEsiPromptWindow : ChromedWindow
{
    public StartFleetEsiPromptWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public StartFleetEsiPromptWindow(string fleetName, int unlinkedCount) : this()
    {
        this.FindControl<TextBlock>("BodyBlock")!.Text =
            $"'{fleetName}' has {unlinkedCount} member(s) without an ESI link. Starting flips the fleet to Active.";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
