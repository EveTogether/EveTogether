using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.Views;

/// <summary>The FITS fit-browser window, opened non-modally from the main window so the Local library and
/// the live server tabs stay usable alongside it.</summary>
public partial class FitBrowserWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public FitBrowserWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FitBrowserWindow(FitBrowserViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }

    /// <summary>Puts the caret back in the search box after its clear cross has wiped the filter, so the next thing
    /// typed goes where it was going anyway. The wiping itself is the view-model's (ClearSearchCommand); this is only
    /// the focus, which is the view's business. The button sits in the box's own InnerRightContent, so the box is
    /// simply its nearest TextBox ancestor.</summary>
    private void OnSearchCleared(object? sender, RoutedEventArgs e)
    {
        if (sender is Visual button && button.FindAncestorOfType<TextBox>() is { } box) box.Focus();
    }

    /// <summary>Opens a card's fit. A card is one thing that opens one window, so a single click does it. Clicks that
    /// started inside the card's own buttons are left alone: the export and manage menus must not open the fit behind
    /// them.</summary>
    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        if (sender is Control { DataContext: FitRowViewModel row } && DataContext is FitBrowserViewModel vm)
        {
            if (vm.SelectedTab is { } tab) tab.SelectedRow = row;
            vm.OpenDetailCommand.Execute(row);
        }
    }

    /// <summary>Loads the equipment icons for a card's popover the first time the cursor enters that card — a card
    /// nobody hovers fetches no module images at all.</summary>
    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: FitRowViewModel row })
            _ = row.LoadPopoverIconsAsync();
    }
}
