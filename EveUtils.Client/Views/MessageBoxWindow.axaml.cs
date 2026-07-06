using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Simple modal message box. Default = OK only (info). In confirm mode it shows Cancel + OK and
/// returns a bool result (true = confirmed) via ShowDialog&lt;bool&gt;. Title and message are set
/// in code-behind after the XAML is loaded — an ElementName binding to a plain property reads an empty
/// string at load time (the value is assigned after), which rendered the dialog blank.
/// </summary>
public partial class MessageBoxWindow : ChromedWindow
{
    public MessageBoxWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MessageBoxWindow(string title, string message, bool confirm = false, string okText = "OK", string? optOutText = null) : this()
    {
        Title = string.IsNullOrWhiteSpace(title) ? "EVE Together" : title;
        this.FindControl<TextBlock>("TitleBlock")!.Text = title;
        this.FindControl<TextBlock>("MessageBlock")!.Text = message;
        if (confirm)
        {
            this.FindControl<Button>("CancelButton")!.IsVisible = true;
            this.FindControl<Button>("OkButton")!.Content = okText;
        }
        if (!string.IsNullOrWhiteSpace(optOutText))
        {
            var check = this.FindControl<CheckBox>("OptOutCheck")!;
            check.Content = optOutText;
            check.IsVisible = true;
        }
    }

    /// <summary>Whether the "don't ask again" opt-out checkbox was ticked (only meaningful when an opt-out text was set).</summary>
    public bool OptOutChecked => this.FindControl<CheckBox>("OptOutCheck")!.IsChecked == true;

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
