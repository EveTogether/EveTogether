using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>
/// Modal dialog to edit a local fit's metadata (fit-metadata): name, comma-separated tags and a free-text description.
/// Prefilled with the current values; returns the edited <see cref="FitMetadataDraft"/> via <c>ShowDialog</c> on Save
/// (null on Cancel or an empty name). The fit's modules and identity are untouched — this only edits the metadata.
/// </summary>
public partial class FitMetadataDialog : ChromedWindow
{
    public FitMetadataDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FitMetadataDialog(FitMetadataDraft current) : this()
    {
        this.FindControl<TextBox>("NameInput")!.Text = current.Name;
        this.FindControl<TextBox>("TagsInput")!.Text = current.Tags;
        this.FindControl<TextBox>("DescriptionInput")!.Text = current.Description;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NameInput")?.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            return;   // a fit must keep a name; leave the dialog open so the user can fix it

        Close(new FitMetadataDraft(name, _NullIfBlank("DescriptionInput"), _NullIfBlank("TagsInput")));
    }

    private string? _NullIfBlank(string controlName)
    {
        var text = this.FindControl<TextBox>(controlName)?.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
