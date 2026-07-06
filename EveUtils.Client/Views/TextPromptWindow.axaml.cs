using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Single-line text prompt: a header + one text box, returning the entered value via
/// ShowDialog&lt;string?&gt; on OK (trimmed; null if empty or cancelled). Used for the add-wing / add-squad name
/// prompts. Header + default are set in code-behind after the XAML loads (an ElementName binding reads an empty
/// string at load time, before the value is assigned — see MessageBoxWindow).
/// </summary>
public partial class TextPromptWindow : ChromedWindow
{
    public TextPromptWindow()
    {
        AvaloniaXamlLoader.Load(this);
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
