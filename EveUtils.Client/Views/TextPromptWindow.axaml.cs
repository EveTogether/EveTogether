using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Single-line text prompt: a header + one text box, returning the entered value via
/// ShowDialog&lt;string?&gt; on OK (trimmed; null if empty or cancelled). Used for the add-wing / add-squad name
/// prompts and for naming an EVE account. Header + default are set in code-behind after the XAML loads (an
/// ElementName binding reads an empty string at load time, before the value is assigned — see MessageBoxWindow).
///
/// Enter confirms and Escape cancels (the OK/Cancel buttons carry <c>IsDefault</c>/<c>IsCancel</c>), and the box
/// takes focus with its text selected, so the whole prompt is one typed line without touching the mouse.
/// </summary>
public partial class TextPromptWindow : ChromedWindow
{
    public TextPromptWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Focus only lands once the window is up; asking for it in the constructor is too early.
        Opened += (_, _) =>
        {
            var box = this.FindControl<TextBox>("ValueBox");
            box?.Focus();
            box?.SelectAll();
        };
    }

    public TextPromptWindow(string title, string header, string? defaultValue = null) : this()
    {
        Title = string.IsNullOrWhiteSpace(title) ? "EVE Together" : title;
        this.FindControl<TextBlock>("HeaderBlock")!.Text = header;
        var box = this.FindControl<TextBox>("ValueBox")!;
        box.Text = defaultValue ?? string.Empty;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var value = this.FindControl<TextBox>("ValueBox")?.Text?.Trim();
        Close(string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
