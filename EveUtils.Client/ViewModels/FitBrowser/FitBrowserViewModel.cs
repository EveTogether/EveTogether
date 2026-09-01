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
/// server). Selecting a server tab loads its rows lazily; clicking a card opens the radial
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
    private readonly Func<FitSortChoice, Task>? _saveSort;
    private bool _sortChosen;
    private bool _applySortSuspended;

    /// <summary>The setting the chosen order is remembered under. A key of its own: the CARDS/LIST preference this
    /// screen used to keep was removed with the table (ET-112) and must not come back to life here.</summary>
    public const string SortSettingKey = "ui.fit-browser.sort";

    public FitBrowserViewModel(
        IEnumerable<FitBrowserTabViewModel> tabs,
        Func<FitRowViewModel, Task>? openDetail = null,
        Func<Task>? importEsi = null,
        Func<Task>? importText = null,
        Func<Task>? importEsfLink = null,
        Func<Task>? refresh = null,
        Func<Task<FitSortChoice?>>? loadSort = null,
        Func<FitSortChoice, Task>? saveSort = null)
    {
        _openDetail = openDetail;
        _importEsi = importEsi;
        _importText = importText;
        _importEsfLink = importEsfLink;
        _refresh = refresh;
        _saveSort = saveSort;
        // A server coupled while the browser is open gets its tab appended straight to this collection
        // (MainWindowViewModel's RefreshAsync), so the order is handed to tabs as they arrive rather than only here.
        Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.NewItems?.OfType<FitBrowserTabViewModel>() ?? [])
                tab.ApplySort(Sort, SortDescending);
        };
        foreach (var tab in tabs) Tabs.Add(tab);
        SelectedTab = Tabs.FirstOrDefault();
        if (loadSort is not null) _ = RestoreSortAsync(loadSort);
    }

    // ── the order, for every tab at once ─────────────────────────────────────────────────────────────────────────

    /// <summary>What the cards are ordered by. The browser's, not a tab's: changing source keeps your order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    [NotifyPropertyChangedFor(nameof(IsSortByName))]
    [NotifyPropertyChangedFor(nameof(IsSortByPrice))]
    [NotifyPropertyChangedFor(nameof(IsSortByHullClass))]
    private FitSortOrder _sort = FitSortOrder.Name;

    [ObservableProperty] private bool _sortDescending;

    /// <summary>What the sort button says, so the chosen order is readable without opening the menu.</summary>
    public string SortLabel => Sort switch
    {
        FitSortOrder.Price => "Price",
        FitSortOrder.HullClass => "Hull class",
        _ => "Fit name"
    };

    public bool IsSortByName => Sort is FitSortOrder.Name;
    public bool IsSortByPrice => Sort is FitSortOrder.Price;
    public bool IsSortByHullClass => Sort is FitSortOrder.HullClass;

    /// <summary>Order by this field, and remember it for the next session.</summary>
    [RelayCommand]
    private void SetSort(FitSortOrder sort)
    {
        _sortChosen = true;
        if (sort == Sort) return;

        Sort = sort;
        SaveSort();
    }

    /// <summary>Turn the current order round.</summary>
    [RelayCommand]
    private void ToggleSortDirection()
    {
        _sortChosen = true;
        SortDescending = !SortDescending;
        SaveSort();
    }

    partial void OnSortChanged(FitSortOrder value) => ApplySort();

    partial void OnSortDescendingChanged(bool value) => ApplySort();

    private void ApplySort()
    {
        if (_applySortSuspended) return;
        foreach (var tab in Tabs) tab.ApplySort(Sort, SortDescending);
    }

    private void SaveSort()
    {
        if (_saveSort is not null) _ = _saveSort(new FitSortChoice(Sort, SortDescending));
    }

    /// <summary>Restores the remembered order. It lands asynchronously, so a choice that beat it wins — restoring
    /// must never overwrite what the user just picked with the value that pick replaced. Both halves are set before
    /// anything is applied, so the cards are re-ordered once rather than once per half.</summary>
    private async Task RestoreSortAsync(Func<Task<FitSortChoice?>> loadSort)
    {
        var stored = await loadSort();
        if (stored is not { } choice || _sortChosen) return;

        _applySortSuspended = true;
        Sort = choice.Order;
        SortDescending = choice.Descending;
        _applySortSuspended = false;
        ApplySort();
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

    /// <summary>Manual refresh of the selected tab: the Local tab re-reads the DB (and picks up any newly coupled
    /// server, same as <see cref="RefreshModule"/>); a server tab re-fetches regardless of whether it was already
    /// loaded — a refresh that only re-shows cached rows would look like it worked while showing stale data.</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        if (SelectedTab is null) return;
        if (SelectedTab.IsLocal) { if (_refresh is not null) await _refresh(); }
        else await SelectedTab.ReloadAsync();
    }

    partial void OnSelectedTabChanged(FitBrowserTabViewModel? value)
    {
        if (value is not null) _ = value.EnsureLoadedAsync();
    }

    /// <summary>Opens the radial detail window for a row (its card was clicked).</summary>
    [RelayCommand]
    private async Task OpenDetail(FitRowViewModel? row)
    {
        if (row is not null && _openDetail is not null) await _openDetail(row);
    }
}
