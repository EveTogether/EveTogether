using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Skills.Plans;

namespace EveUtils.Client.Views.Skills;

/// <summary>The + FROM DOCTRINE… picker (ET-386): composition, role, then entry. Closes with the picked entry on
/// SELECT, or null on Cancel/close.</summary>
public partial class DoctrinePickerWindow : ChromedWindow
{
    public DoctrinePickerWindow() => AvaloniaXamlLoader.Load(this);

    public DoctrinePickerWindow(DoctrinePickerViewModel viewModel) : this() => DataContext = viewModel;

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DoctrinePickerViewModel vm)
        {
            Close(vm.BuildPick());
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
