using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EveUtils.Client.Views;

/// <summary>
/// A text box that takes the keyboard, with its text selected, the moment it is shown — for an editor that only exists
/// on demand (a homefront payout typed over the table's, ET-271): clicking the figure and then having to click again
/// into the field it opened is the step this saves.
/// </summary>
public sealed class FocusWhenShown : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<FocusWhenShown, TextBox, bool>("IsEnabled");

    static FocusWhenShown() =>
        Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, _) =>
        {
            if (!GetIsEnabled(box) || !box.IsVisible)
                return;

            // After the layout pass that shows it: focus handed to a box not yet arranged is dropped.
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        });

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);
}
