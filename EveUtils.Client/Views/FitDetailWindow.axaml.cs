using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.Views;

/// <summary>The radial fit-detail window: the fitting wheel plus the Dogma-computed stats panels,
/// opened non-modally by double-clicking a fit in the browser. Charges can be dragged from the Charges strip onto a
/// module (load there) or onto the wheel centre (load on every module that accepts it) — 2f.</summary>
public partial class FitDetailWindow : ChromedWindow, IHostableModuleWindow
{
    // DataFormat<T> needs a reference type, so the charge type id travels as a string.
    private static readonly DataFormat<string> ChargeFormat = DataFormat.CreateInProcessFormat<string>("eveutils-charge-type-id");

    public Action? CloseRequested { get; set; }

    public FitDetailWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Attach the charge drag/drop handlers to the content root (not the window) so they survive being re-hosted
        // inside the docked module host, where this window itself is never shown.
        if (Content is Control content)
        {
            content.AddHandler(DragDrop.DragOverEvent, OnChargeDragOver);
            content.AddHandler(DragDrop.DropEvent, OnChargeDrop);
        }
    }

    public FitDetailWindow(FitDetailWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }

    // A charge is placed on the fit only by dragging it (Avalonia 12 only lets a drag begin from a press event, so we
    // start one on every left press). The drop loads the charge — onto a single module (drop on it) or on every module
    // of the type (drop on the wheel centre), via OnChargeDrop (2f). A plain click (no drag) intentionally does nothing:
    // the Charges list is filtered by the module-type icons above it, not by clicking a charge row.
    private async void OnChargePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DraggableChargeViewModel charge } ||
            !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ChargeFormat, charge.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    // Load a cargo item into every fitted module that accepts it as a charge. Cargo loads by click, not drag: a drag
    // started in the cargo Popup can't reach the wheel across the popup→window top-level boundary.
    // An incompatible item loads nowhere.
    private void OnCargoLoad(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: CargoItemViewModel cargo } && DataContext is FitDetailWindowViewModel viewModel)
            _ = viewModel.LoadChargeOnAllAsync(cargo.TypeId);
    }

    private void OnChargeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(ChargeFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Drop onto a module → load the charge there; drop elsewhere on the wheel → load it on every module that accepts it.
    private void OnChargeDrop(object? sender, DragEventArgs e)
    {
        var raw = e.DataTransfer.TryGetValue(ChargeFormat);
        if (raw is null ||
            !int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var chargeTypeId))
            return;

        var module = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<ModuleSlotViewModel>()
            .FirstOrDefault();
        if (module is not null)
        {
            _ = module.LoadChargeAsync(chargeTypeId);
            e.Handled = true;
            return;
        }

        if (DataContext is FitDetailWindowViewModel viewModel)
        {
            _ = viewModel.LoadChargeOnAllAsync(chargeTypeId);
            e.Handled = true;
        }
    }
}
