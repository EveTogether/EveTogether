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

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FitBrowserViewModel vm && vm.SelectedTab?.SelectedRow is { } row)
            vm.OpenDetailCommand.Execute(row);
    }

    /// <summary>Opens a card's fit. A card is one thing that opens one window, so a single click does it — the
    /// table keeps its double-click, where a click is how you select a row. Clicks that started inside the card's
    /// own buttons are left alone: the export and manage menus must not open the fit behind them.</summary>
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

    /// <summary>Loads a rack's per-module icons the first time the cursor enters its "x modules" cell, so the tooltip
    /// can show icons without every row fetching images up front.</summary>
    private void OnRackPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: FitRowViewModel row, Tag: string rack })
        {
            var category = rack switch
            {
                "High" => FitSlotCategory.High,
                "Medium" => FitSlotCategory.Medium,
                "Low" => FitSlotCategory.Low,
                _ => FitSlotCategory.Other
            };
            _ = row.LoadRackIconsAsync(category);
        }
    }
}
