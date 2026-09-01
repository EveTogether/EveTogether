using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One tab in the fit-browser: the fits of one source (the Local library or a coupled server), with search, an
/// order, and client-side paging (10/25/50/100), shown as a grid of cards. The selected row drives the shared detail
/// panel. Like <see cref="FittingsTabViewModel"/>, server tabs load their rows lazily on first selection, and a page
/// pulls in its own images rather than the whole library's.
///
/// The order is the browser's, not the tab's: <see cref="FitBrowserViewModel"/> owns the choice and pushes it into
/// every tab through <see cref="ApplySort"/>, so switching source does not switch order.
/// </summary>
public partial class FitBrowserTabViewModel : ObservableObject
{
    public static IReadOnlyList<int> PageSizeOptions { get; } = [10, 25, 50, 100];

    public string Header { get; }
    public bool IsLocal { get; }
    public string? ServerAddress { get; }

    /// <summary>Page of rows currently shown in the card grid (after search + paging).</summary>
    public ObservableCollection<FitRowViewModel> PagedRows { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _pageSize = 25;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private FitRowViewModel? _selectedRow;
    [ObservableProperty] private FitDetailViewModel? _detail;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoaded;

    /// <summary>What the cards are ordered by, and which way round. Set by the browser through
    /// <see cref="ApplySort"/> — the tab never chooses it itself, so nothing in the view binds to these.</summary>
    public FitSortOrder Sort { get; private set; } = FitSortOrder.Name;

    public bool SortDescending { get; private set; }

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

    /// <summary>Replaces the tab's full row set (after a load) and re-applies search + order + paging.</summary>
    public void SetRows(IEnumerable<FitRowViewModel> rows)
    {
        _allRows = rows.ToList();
        CurrentPage = 1;
        Refresh();
        // A tab whose rows have just arrived is appearing anyway, so it shows them straight away and re-orders once
        // when the prices catch up, rather than staying blank until they do.
        WaitForPrices();
    }

    /// <summary>The order the browser wants, applied to this tab's rows. Both halves land in one go, so a change of
    /// field and direction together rebuilds the page once.</summary>
    public void ApplySort(FitSortOrder sort, bool descending)
    {
        if (sort == Sort && descending == SortDescending) return;

        Sort = sort;
        SortDescending = descending;
        CurrentPage = 1;
        if (WaitForPrices()) return;
        Refresh();
    }

    /// <summary>
    /// Ordering by a figure that has not arrived yet. Prices are fetched per row and land whenever the cache answers,
    /// so sorting on them the moment the user asks would put the cards in an order built from half the numbers and
    /// then shuffle them under the reader's eye as the rest came in. Instead the page is left exactly as it is until
    /// every outstanding price is in, and then re-ordered ONCE — the only movement is the one the click asked for.
    /// The rows already started their fetch when they were built and <see cref="FitRowViewModel.LoadPriceAsync"/>
    /// hands back that same task, so this waits on work in flight rather than starting it again.
    ///
    /// Answers true when it took the ordering over. A fit whose price never resolves (no repository, or a price cache
    /// that has never been filled) keeps its placeholder and sorts last either way — see <see cref="Order"/>.
    /// </summary>
    private bool WaitForPrices()
    {
        if (Sort is not FitSortOrder.Price) return false;

        var pending = _allRows.Where(row => row.Price is null).ToList();
        if (pending.Count == 0) return false;

        _ = WaitThenRefreshAsync(pending);
        return true;
    }

    private async Task WaitThenRefreshAsync(IReadOnlyList<FitRowViewModel> pending)
    {
        await Task.WhenAll(pending.Select(row => row.LoadPriceAsync()));
        if (Sort is FitSortOrder.Price) Refresh();
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

    /// <summary>Re-fetches a server tab's rows regardless of <see cref="IsLoaded"/> — the manual refresh button and
    /// the post-share auto-refresh both need the current server state, not "already loaded once".</summary>
    public async Task ReloadAsync()
    {
        if (IsLocal || _loader is null) return;
        IsLoaded = true;
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
        _filtered = Order(string.IsNullOrWhiteSpace(Search)
            ? _allRows
            : _allRows.Where(r => _Matches(r, Search.Trim())));

        if (CurrentPage > PageCount) CurrentPage = PageCount;

        var page = _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
        PagedRows.Clear();
        foreach (var row in page) PagedRows.Add(row);
        FillPage(page);

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(PageInfo));
    }

    /// <summary>
    /// Fetches what a page costs to show — the hull renders and the uploaders' portraits — for the fits on this
    /// page and no others. All of it is IO and all of it is fire-and-forget: the providers collapse duplicate hulls
    /// and duplicate pilots into a single download each, so a slow or unreachable image server delays pictures and
    /// nothing else on the screen. Turning the page starts the next one's; a row that already has its images does
    /// nothing.
    /// </summary>
    private void FillPage(IReadOnlyList<FitRowViewModel> page)
    {
        foreach (var row in page)
        {
            _ = row.LoadHullRenderAsync();
            _ = row.LoadUploaderPortraitAsync();
        }
    }

    /// <summary>
    /// The order the cards are drawn in. Ordinal comparisons throughout: the library holds EVE's own names, and a
    /// browser must not put a fit in a different place because the machine is set to Dutch (ET-34).
    ///
    /// A row with nothing to sort on — no price yet, or a hull the SDE cannot classify — goes last in BOTH
    /// directions. Flipping the arrow is meant to turn the answer round, not to promote the fits that have no answer
    /// to the top. Ties fall back to the fit's name, so equal rows keep one fixed order instead of drifting between
    /// refreshes.
    /// </summary>
    private List<FitRowViewModel> Order(IEnumerable<FitRowViewModel> rows) => Sort switch
    {
        FitSortOrder.Price => (SortDescending
                ? rows.OrderByDescending(r => r.Price.HasValue).ThenByDescending(r => r.Price ?? 0d)
                : rows.OrderByDescending(r => r.Price.HasValue).ThenBy(r => r.Price ?? 0d))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),

        FitSortOrder.HullClass => (SortDescending
                ? rows.OrderByDescending(r => r.HasHullClass).ThenByDescending(r => r.HullClass, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.HasHullClass).ThenBy(r => r.HullClass, StringComparer.OrdinalIgnoreCase))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),

        _ => (SortDescending
                ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)).ToList()
    };

    /// <summary>Search matches everything the card itself shows — the fit's name, its hull and hull class, who put it
    /// there, and any of its tags — so anything you can read on a card is something you can look for.</summary>
    private static bool _Matches(FitRowViewModel row, string term) =>
        _Has(row.Name, term)
        || _Has(row.ShipTypeLabel, term)
        || _Has(row.HullClass, term)
        || _Has(row.Uploader, term)
        || row.Tags.Any(tag => _Has(tag, term));

    private static bool _Has(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
