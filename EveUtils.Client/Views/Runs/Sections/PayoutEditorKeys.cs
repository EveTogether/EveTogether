using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveUtils.Client.ViewModels.Runs.Attendance;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>
/// Enter keeps a payout typed over the table's, Escape or clicking away drops it (ET-271) — for the one text box a
/// HOMEFRONT row opens on demand, in the run window and on the detail screen alike.
/// </summary>
internal static class PayoutEditorKeys
{
    public static void Attach(UserControl view)
    {
        view.AddHandler(InputElement.KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
        view.AddHandler(InputElement.LostFocusEvent, _OnLostFocus, RoutingStrategies.Bubble);
    }

    private static void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { DataContext: AttendanceRowViewModel { IsEditingPayout: true } row } box
            || !box.Classes.Contains("amountedit"))
            return;

        switch (e.Key)
        {
            case Key.Enter:
                row.CommitPayoutEditCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                row.CancelPayoutEditCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static void _OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: AttendanceRowViewModel { IsEditingPayout: true } row } box
            && box.Classes.Contains("amountedit"))
            row.CancelPayoutEditCommand.Execute(null);
    }
}
