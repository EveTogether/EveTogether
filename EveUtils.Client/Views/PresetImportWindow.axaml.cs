using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EveUtils.Client.EveSettings;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Read-a-preset dialog (ET-61). Picking the file is the only thing that belongs to the window; reading it, showing
/// what it would do and writing it are all the view-model's, so the whole import is testable without a screen.
/// </summary>
public partial class PresetImportWindow : ChromedWindow
{
    public PresetImportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PresetImportWindow(PresetImportViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnChoose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PresetImportViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a preset",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("EVE Together preset") { Patterns = ["*" + SettingsPreset.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        if (picked.Count == 0)
            return;

        await viewModel.LoadAsync(picked[0].Path.LocalPath);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
