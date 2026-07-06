using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Per-fleet sharing dialog: per character (or all at once) a three-way override per metric. Returns true on Save so
/// the caller can persist the view model's <see cref="FleetShareViewModel.BuildOverrides"/>; false/closed on cancel.
/// </summary>
public partial class FleetShareWindow : ChromedWindow
{
    public FleetShareWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetShareWindow(FleetShareViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
}
