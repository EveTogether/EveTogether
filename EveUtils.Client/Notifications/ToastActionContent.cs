using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;

namespace EveUtils.Client.Notifications;

/// <summary>
/// Builds the visual content for a toast that carries action buttons. Avalonia's built-in <c>Notification</c> renders
/// only a title + message, so an action toast is shown as plain content instead: the hosting
/// <see cref="NotificationCard"/> supplies the chrome, this builds the title, optional message and a right-aligned row
/// of buttons. Each button uses <see cref="NotificationCard"/>'s <c>CloseOnClick</c> attached property so picking an
/// action dismisses the toast, and runs that action's callback. Kept separate from <see cref="ToastService"/> so it
/// can be unit-tested without a window's overlay layer.
/// </summary>
public static class ToastActionContent
{
    /// <summary>Builds the content control for a toast with <paramref name="actions"/> rendered as buttons.</summary>
    public static Control Build(string title, string? message, ToastKind kind, IReadOnlyList<ToastAction> actions)
    {
        var layout = new StackPanel { Spacing = 6 };

        layout.Children.Add(new TextBlock
        {
            Text = KindGlyph(kind) + title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrEmpty(message))
            layout.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, MinWidth = 0 };
            button.Classes.Add("toast-action"); // compact sizing (the form-button default overflows the card)
            if (StyleClass(action.Style) is { } styleClass)
                button.Classes.Add(styleClass); // green (good) / red (danger) tint via the themed button classes
            NotificationCard.SetCloseOnClick(button, true); // picking an action dismisses the toast
            var chosen = action;                            // capture per iteration for the click handler
            button.Click += (_, _) => chosen.Run();
            buttons.Children.Add(button);
        }

        layout.Children.Add(buttons);

        // The card gives custom content no padding and does not bound its width, so a toast otherwise sits flush
        // against the border with its title running off-screen — inset + cap the width here.
        return new Border { Padding = new Thickness(14, 12), MinWidth = 240, MaxWidth = 340, Child = layout };
    }

    private static string? StyleClass(ToastActionStyle style) => style switch
    {
        ToastActionStyle.Affirmative => "good",
        ToastActionStyle.Destructive => "danger",
        _ => null,
    };

    // A leading severity cue. Success is the quiet default (no glyph); the rest match the plain toast's intent without
    // inventing theme colours (the card chrome is themed by the host) — see Languages/AvaloniaUI.md.
    private static string KindGlyph(ToastKind kind) => kind switch
    {
        ToastKind.Warning => "⚠ ",     // ⚠
        ToastKind.Error => "⛔ ",       // ⛔
        ToastKind.Information => "ℹ ",  // ℹ
        _ => "",
    };
}
