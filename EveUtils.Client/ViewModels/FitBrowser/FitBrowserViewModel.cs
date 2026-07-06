using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// View-model for the FITS fit-browser window: one tab per source (Local first, then a tab per coupled
/// server). Selecting a server tab loads its rows lazily; double-clicking a row opens the radial
/// detail window via the injected <c>openDetail</c> callback. The view-model is pure — the rows, server
/// loaders and detail opener are supplied by <see cref="MainWindowViewModel"/>, so it stays unit-testable.
/// </summary>
public partial class FitBrowserViewModel : ObservableObject
{
    public ObservableCollection<FitBrowserTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private FitBrowserTabViewModel? _selectedTab;

    private readonly Func<FitRowViewModel, Task>? _openDetail;
    private readonly Func<Task>? _importEsi;
    private readonly Func<Task>? _importText;
    private readonly Func<Task>? _importEsfLink;

    public FitBrowserViewModel(
        IEnumerable<FitBrowserTabViewModel> tabs,
        Func<FitRowViewModel, Task>? openDetail = null,
        Func<Task>? importEsi = null,
        Func<Task>? importText = null,
        Func<Task>? importEsfLink = null)
    {
        _openDetail = openDetail;
        _importEsi = importEsi;
        _importText = importText;
        _importEsfLink = importEsfLink;
        foreach (var tab in tabs) Tabs.Add(tab);
        SelectedTab = Tabs.FirstOrDefault();
    }

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
