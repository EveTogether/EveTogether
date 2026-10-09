using Avalonia.Controls;
using Avalonia.Input;
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

    // A three-answer box hands back bool? and cancel is null; a plain confirm keeps its bool and cancel is false.
    private bool _threeWay;
    private bool _escapeCancels;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_escapeCancels && e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancel(this, new RoutedEventArgs());
            return;
        }

        base.OnKeyDown(e);
    }

    public MessageBoxWindow(string title, string message, bool confirm = false, string okText = "OK",
        string? optOutText = null, string? secondaryText = null, bool offerCancel = true,
        bool? defaultAnswer = null) : this()
    {
        Title = string.IsNullOrWhiteSpace(title) ? "EVE Together" : title;
        this.FindControl<TextBlock>("TitleBlock")!.Text = title;
        this.FindControl<TextBlock>("MessageBlock")!.Text = message;
        if (confirm)
        {
            this.FindControl<Button>("CancelButton")!.IsVisible = offerCancel;
            this.FindControl<Button>("OkButton")!.Content = okText;
        }
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            _threeWay = true;
            var secondary = this.FindControl<Button>("SecondaryButton")!;
            secondary.Content = secondaryText;
            secondary.IsVisible = true;
        }
        if (defaultAnswer is { } primaryIsDefault)
        {
            // Opt-in: every other dialog keeps no default button, so Enter never confirms something destructive.
            this.FindControl<Button>(primaryIsDefault || !_threeWay ? "OkButton" : "SecondaryButton")!.IsDefault = true;
            _escapeCancels = true;
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
    private void OnSecondary(object? sender, RoutedEventArgs e) => Close(false);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(_threeWay ? null : false);
}
