using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The reusable fit picker dialog. In multi mode it closes with the selected fits' snapshots on ADD; in
/// single mode it closes with the one picked fit immediately (the view-model raises
/// <see cref="FitPickerViewModel.FitPicked"/>). Cancel closes with null.
/// </summary>
public partial class FitPickerWindow : ChromedWindow
{
    public FitPickerWindow() => AvaloniaXamlLoader.Load(this);

    public FitPickerWindow(FitPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.FitPicked += fit => Close(fit);   // single-select: close with the picked fit
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FitPickerViewModel { CanConfirm: true } vm)
            Close(vm.SelectedFits());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
