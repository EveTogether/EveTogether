using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Per-character settings dialog, opened from the gear button on a character row. The
/// <see cref="CharacterDialogViewModel"/> is its DataContext; the VM is disposed when the window closes
/// (it unsubscribes from the live bus-state feed).
/// </summary>
public partial class CharacterWindow : ChromedWindow
{
    public CharacterWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CharacterWindow(CharacterDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Title = string.IsNullOrWhiteSpace(viewModel.Name) ? "Character" : viewModel.Name;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
