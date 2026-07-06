using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Modal dialog for importing a fit from an eveship.fit (ESF) link. Returns the pasted link via
/// <c>ShowDialog&lt;string?&gt;</c> on Import (null on Cancel or empty); the link is decoded by the same fit-text
/// importer as the EFT/DNA window, so this window only collects the URL.
/// </summary>
public partial class FitEsfImportWindow : ChromedWindow
{
    public FitEsfImportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        var link = this.FindControl<TextBox>("LinkInput")?.Text?.Trim();
        Close(string.IsNullOrWhiteSpace(link) ? null : link);
    }
}
