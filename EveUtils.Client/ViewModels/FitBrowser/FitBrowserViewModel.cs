using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// View-model for the FITS fit-browser window: one tab per source (Local first, then a tab per coupled
/// server). Selecting a server tab loads its rows lazily; double-clicking a row opens the radial
/// detail window via the injected <c>openDetail</c> callback. The view-model is pure — the rows, server
/// loaders and detail opener are supplied by <see cref="MainWindowViewModel"/>, so it stays unit-testable.
/// </summary>
public partial class FitBrowserViewModel : ObservableObject, IRefreshableModule
{
    public ObservableCollection<FitBrowserTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private FitBrowserTabViewModel? _selectedTab;

    private readonly Func<FitRowViewModel, Task>? _openDetail;
    private readonly Func<Task>? _importEsi;
    private readonly Func<Task>? _importText;
    private readonly Func<Task>? _importEsfLink;
    private readonly Func<Task>? _refresh;
    private readonly Func<FitBrowserLayout, Task>? _saveLayout;
    private bool _layoutChosen;

    /// <summary>The setting the chosen density is remembered under.</summary>
    public const string LayoutSettingKey = "ui.fit-browser.layout";

    public FitBrowserViewModel(
        IEnumerable<FitBrowserTabViewModel> tabs,
        Func<FitRowViewModel, Task>? openDetail = null,
        Func<Task>? importEsi = null,
        Func<Task>? importText = null,
        Func<Task>? importEsfLink = null,
        Func<Task>? refresh = null,
        Func<Task<FitBrowserLayout?>>? loadLayout = null,
        Func<FitBrowserLayout, Task>? saveLayout = null)
    {
        _openDetail = openDetail;
        _importEsi = importEsi;
        _importText = importText;
        _importEsfLink = importEsfLink;
        _refresh = refresh;
        _saveLayout = saveLayout;
        foreach (var tab in tabs) Tabs.Add(tab);
        SelectedTab = Tabs.FirstOrDefault();
        if (loadLayout is not null) _ = RestoreLayoutAsync(loadLayout);
    }

    /// <summary>How the fits are drawn. Cards by default — the hull render is what a fit is recognised by; the
    /// table is a click away for sorting a column or reading prices side by side.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardLayout))]
    [NotifyPropertyChangedFor(nameof(IsListLayout))]
    private FitBrowserLayout _layout = FitBrowserLayout.Cards;

    public bool IsCardLayout => Layout is FitBrowserLayout.Cards;
    public bool IsListLayout => Layout is FitBrowserLayout.List;

    /// <summary>Switch the density and remember it for the next session.</summary>
    [RelayCommand]
    private void SetLayout(FitBrowserLayout layout)
    {
        _layoutChosen = true;
        if (layout == Layout) return;

        Layout = layout;
        if (_saveLayout is not null) _ = _saveLayout(layout);
    }

    /// <summary>Restores the remembered density. It lands asynchronously, so a click that beat it wins — restoring
    /// must never overwrite the choice the user just made with the value that choice replaced (same rule as
    /// <see cref="FleetMetricsViewModel"/>'s).</summary>
    private async Task RestoreLayoutAsync(Func<Task<FitBrowserLayout?>> loadLayout)
    {
        var stored = await loadLayout();
        if (stored is { } layout && !_layoutChosen) Layout = layout;
    }

    /// <summary>
    /// The browser is one module for the whole app (not per-entity), so re-opening it re-selects this standing
    /// instance instead of building a fresh one — and a fresh one is exactly what would have picked up a fit
    /// imported elsewhere or a server coupled after this screen was first opened. <paramref name="refresh"/> (built
    /// by <see cref="MainWindowViewModel"/>, which owns the local-fit and coupled-server reads) re-syncs this
    /// instance the same way (ET-48, same pattern as ET-46).
    /// </summary>
    public void RefreshModule() => _ = _refresh?.Invoke();

    /// <summary>The browser is the single fittings surface, so it owns the import actions.</summary>
    public bool CanImport => _importEsi is not null || _importText is not null || _importEsfLink is not null;

    [RelayCommand]
    private async Task ImportFromEsi() { if (_importEsi is not null) await _importEsi(); }

    [RelayCommand]
    private async Task ImportText() { if (_importText is not null) await _importText(); }

    [RelayCommand]
    private async Task ImportEsfLink() { if (_importEsfLink is not null) await _importEsfLink(); }

    partial void OnSelectedTabChanged(FitBrowserTabViewModel? value)
    {
        if (value is not null) _ = value.EnsureLoadedAsync();
    }

    /// <summary>Opens the radial detail window for a row (double-clicked in the grid).</summary>
    [RelayCommand]
    private async Task OpenDetail(FitRowViewModel? row)
    {
        if (row is not null && _openDetail is not null) await _openDetail(row);
    }
}
