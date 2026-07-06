using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One tab in the fit-browser: a DataGrid of fits for one source (the Local library or a coupled server),
/// with name-search and client-side paging (10/25/50/100). The selected row drives the shared detail panel. Like
/// <see cref="FittingsTabViewModel"/>, server tabs load their rows lazily on first selection.
/// </summary>
public partial class FitBrowserTabViewModel : ObservableObject
{
    public static IReadOnlyList<int> PageSizeOptions { get; } = [10, 25, 50, 100];

    public string Header { get; }
    public bool IsLocal { get; }
    public string? ServerAddress { get; }

    /// <summary>Page of rows currently shown in the grid (after search + paging).</summary>
    public ObservableCollection<FitRowViewModel> PagedRows { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _pageSize = 25;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private FitRowViewModel? _selectedRow;
    [ObservableProperty] private FitDetailViewModel? _detail;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoaded;

    private List<FitRowViewModel> _allRows = [];
    private List<FitRowViewModel> _filtered = [];
    private readonly Func<FitBrowserTabViewModel, Task>? _loader;
    private readonly ISdeNameResolver _names;

    /// <summary>Local tab — rows are known up front.</summary>
    public FitBrowserTabViewModel(string header, IEnumerable<FitRowViewModel> rows, ISdeNameResolver? names = null)
    {
        Header = header;
        IsLocal = true;
        IsLoaded = true;
        _names = names ?? FallbackNameResolver.Instance;
        SetRows(rows);
    }

    /// <summary>Server tab — rows are fetched lazily via <paramref name="loader"/> on first selection.</summary>
    public FitBrowserTabViewModel(string header, string serverAddress, Func<FitBrowserTabViewModel, Task> loader, ISdeNameResolver? names = null)
    {
        Header = header;
        IsLocal = false;
        ServerAddress = serverAddress;
        Status = "Select to load…";
        _loader = loader;
        _names = names ?? FallbackNameResolver.Instance;
        Refresh();
    }

    public bool HasDetail => Detail is not null;
    public int FilteredCount => _filtered.Count;
    public int TotalCount => _allRows.Count;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
    public bool IsEmpty => _allRows.Count == 0;
    public bool CanPrev => CurrentPage > 1;
    public bool CanNext => CurrentPage < PageCount;
    public string PageInfo => $"page {CurrentPage} / {PageCount} · {_filtered.Count} fit(s)";

    /// <summary>Replaces the tab's full row set (after a load) and re-applies search + paging.</summary>
    public void SetRows(IEnumerable<FitRowViewModel> rows)
    {
        _allRows = rows.ToList();
        CurrentPage = 1;
        Refresh();
    }

    /// <summary>Finds a row by exact fit name across the full set.</summary>
    public FitRowViewModel? FindByName(string name) => _allRows.FirstOrDefault(r => r.Name == name);

    /// <summary>Loads a server tab's rows the first time it is shown.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded || IsLocal || _loader is null) return;
        IsLoaded = true; // set first so a slow load isn't started twice on rapid re-selection
        await _loader(this);
    }

    partial void OnSearchChanged(string value)
    {
        CurrentPage = 1;
        Refresh();
    }

    partial void OnPageSizeChanged(int value)
    {
        CurrentPage = 1;
        Refresh();
    }

    partial void OnSelectedRowChanged(FitRowViewModel? value) =>
        Detail = value is null ? null : new FitDetailViewModel(value.Fit, _names);

    partial void OnDetailChanged(FitDetailViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    [RelayCommand]
    private void FirstPage() => GoToPage(1);

    [RelayCommand]
    private void PrevPage() => GoToPage(CurrentPage - 1);

    [RelayCommand]
    private void NextPage() => GoToPage(CurrentPage + 1);

    [RelayCommand]
    private void LastPage() => GoToPage(PageCount);

    private void GoToPage(int page)
    {
        CurrentPage = Math.Clamp(page, 1, PageCount);
        Refresh();
    }

    private void Refresh()
    {
        // Search matches the fit name or any of its tags, so typing a tag filters the list down to the fits carrying it.
        _filtered = string.IsNullOrWhiteSpace(Search)
            ? _allRows
            : _allRows.Where(r => _Matches(r, Search.Trim())).ToList();

        if (CurrentPage > PageCount) CurrentPage = PageCount;

        var page = _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
        PagedRows.Clear();
        foreach (var row in page) PagedRows.Add(row);

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(PageInfo));
    }

    private static bool _Matches(FitRowViewModel row, string term) =>
        row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase));
}
