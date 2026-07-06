using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Server info/trust dialog, opened from a coupled-server row's gear button. Shows the address, live
/// status and the pinned TLS cert fingerprint. Returns
/// <c>true</c> via <c>ShowDialog&lt;bool&gt;</c> if the user pressed Decouple, else <c>false</c>. Fields
/// are set in code-behind after the XAML is loaded — an ElementName binding to a plain property reads an
/// empty string at load time (the value is assigned after).
/// </summary>
public partial class ServerTrustWindow : ChromedWindow
{
    public ServerTrustWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ServerTrustWindow(string displayName, string address, string fingerprint, string statusLabel) : this()
    {
        Title = string.IsNullOrWhiteSpace(displayName) ? "Server trust" : displayName;
        this.FindControl<TextBlock>("DisplayNameBlock")!.Text = displayName;
        this.FindControl<TextBlock>("AddressBlock")!.Text = address;
        this.FindControl<TextBlock>("StatusBlock")!.Text = statusLabel;
        this.FindControl<SelectableTextBlock>("FingerprintBlock")!.Text =
            string.IsNullOrWhiteSpace(fingerprint) ? "(not pinned yet)" : fingerprint;
    }

    private void OnDecouple(object? sender, RoutedEventArgs e) => Close(true);
    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
