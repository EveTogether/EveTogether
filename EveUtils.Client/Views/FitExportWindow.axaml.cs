using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Modal export dialog: shows the fit as EFT + DNA text with copy-to-clipboard buttons. Read-only;
/// purely for sharing the fit out.
/// </summary>
public partial class FitExportWindow : ChromedWindow
{
    public FitExportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FitExportWindow(string fitName, string eft, string dna, string eveshipUrl) : this()
    {
        this.FindControl<TextBlock>("TitleBlock")!.Text = $"Export '{fitName}'";
        this.FindControl<TextBox>("EftBox")!.Text = eft;
        this.FindControl<TextBox>("DnaBox")!.Text = dna;
        this.FindControl<TextBox>("EveshipBox")!.Text = eveshipUrl;
    }

    private async void OnCopyEft(object? sender, RoutedEventArgs e) => await CopyAsync(this.FindControl<TextBox>("EftBox")?.Text);

    private async void OnCopyDna(object? sender, RoutedEventArgs e) => await CopyAsync(this.FindControl<TextBox>("DnaBox")?.Text);

    private async void OnCopyEveship(object? sender, RoutedEventArgs e) => await CopyAsync(this.FindControl<TextBox>("EveshipBox")?.Text);

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text) && Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
