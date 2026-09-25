using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// Modal paste dialog for a skill plan's IMPORT FROM TEXT (ET-355). Returns the pasted text via
/// <c>ShowDialog&lt;string?&gt;</c> on Import (null on Cancel or empty); parsing + storing is the caller's job.
/// </summary>
public partial class SkillPlanTextImportWindow : ChromedWindow
{
    public SkillPlanTextImportWindow() : this(null)
    {
    }

    public SkillPlanTextImportWindow(string? initialText)
    {
        AvaloniaXamlLoader.Load(this);

        if (string.IsNullOrEmpty(initialText))
            return;

        var box = this.FindControl<TextBox>("TextBoxInput")!;
        box.Text = initialText;
        box.CaretIndex = initialText.Length;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("TextBoxInput")?.Text?.Trim();
        Close(string.IsNullOrWhiteSpace(text) ? null : text);
    }
}
