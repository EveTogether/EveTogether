using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EveUtils.Client.EveSettings;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Save-a-preset dialog (ET-61). Only the file picker lives here — it needs a window's <c>StorageProvider</c>, which
/// is exactly the sort of thing the view-model stays free of. The picker is resolved from the visual tree rather
/// than from <c>this</c>, because the tool that opened this dialog may itself be a tab inside the main window.
/// </summary>
public partial class PresetExportWindow : ChromedWindow
{
    public PresetExportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PresetExportWindow(PresetExportViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetExportViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save this preset",
            SuggestedFileName = viewModel.SuggestedFileName,
            DefaultExtension = SettingsPreset.FileExtension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("EVE Together preset") { Patterns = ["*" + SettingsPreset.FileExtension] }
            ]
        });
        if (picked is null)
            return;

        await viewModel.ExportToAsync(picked.Path.LocalPath);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
