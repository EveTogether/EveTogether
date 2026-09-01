using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
///
/// Searching waits for a quiet spell rather than running on every keystroke — see <see cref="SearchQuiet"/>.
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

    /// <summary>
    /// How quiet the search box has to go before the filter runs. Filtering per keystroke made the typing itself
    /// lag: one round that lands a full page of cards is around 320 ms on the operator's 148-fit library, so a
    /// fast typist stacked up rounds far faster than they could be served.
    ///
    /// 250 ms is the ticket's guideline and it survives being measured against what a round costs: it is longer
    /// than the gap between keystrokes at any realistic typing speed (a brisk 60 wpm is ~200 ms apart, and the
    /// gaps inside a word are shorter still), so a word typed straight through filters once at the end instead of
    /// once per letter. Going much shorter would let the gaps inside a word through again and bring the stacking
    /// back; going much longer would be felt as the screen ignoring you, and there is no room for that on top of
    /// the round's own 320 ms.
    /// </summary>
    public static readonly TimeSpan SearchQuiet = TimeSpan.FromMilliseconds(250);

    /// <summary>How the quiet spell is waited out. Real time in the app; a test replaces it so it can hold a round
    /// open and release it on purpose rather than sleeping and hoping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } =
        static (quiet, token) => Task.Delay(quiet, token);

    /// <summary>The filter round the last keystroke started — the quiet spell plus the rebuild it leads to.
    /// Completed when nothing is pending. Exposed so a test can await the round instead of racing it.</summary>
    public Task SearchRound { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _round;

    partial void OnSearchChanged(string value)
    {
        Supersede();

        // An emptied box is not a search being typed, it is the whole library being asked for back, so it does not
        // wait. Nothing follows it that a wait could collapse.
        if (string.IsNullOrEmpty(value))
        {
            FilterNow();
            return;
        }

        var round = new CancellationTokenSource();
        _round = round;
        SearchRound = RunSearchRoundAsync(round.Token);
    }

    /// <summary>
    /// Stops the round the previous keystroke started. That covers both halves of the problem: a round still waiting
    /// out its quiet spell is dropped, and a round that has waited but not yet committed is stopped before it builds
    /// a page for a term the typist has already moved past.
    ///
    /// The source is disposed here rather than inside the round: cancelling runs the round's continuation on this
    /// very stack, so a round that disposed the source it was handed would be disposing it from within its own
    /// <see cref="CancellationTokenSource.Cancel()"/>.
    /// </summary>
    private void Supersede()
    {
        var round = _round;
        _round = null;
        SearchRound = Task.CompletedTask;
        if (round is null) return;

        round.Cancel();
        round.Dispose();
    }

    /// <summary>Waits for the typing to stop, then filters — unless another keystroke arrived first, in which case
    /// this round is abandoned without touching the page. The wait resumes on the context the keystroke came in on,
    /// which is the UI thread, so the rebuild happens where the cards live.</summary>
    private async Task RunSearchRoundAsync(CancellationToken token)
    {
        try
        {
            await Wait(SearchQuiet, token);
            if (token.IsCancellationRequested) return;   // typed again while the wait was being handed back
            FilterNow();
        }
        catch (OperationCanceledException)
        {
            // superseded — the keystroke that cancelled this round owns the next one
        }
    }

    private void FilterNow()
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
        if (!IsAlreadyShowing(page))
        {
            PagedRows.Clear();
            foreach (var row in page) PagedRows.Add(row);
            FillPage(page);
        }

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(PageInfo));
    }

    /// <summary>
    /// Whether the page just worked out is the one already on screen, row for row. Emptying and refilling
    /// <see cref="PagedRows"/> throws away a card's visual and builds it again, measured at roughly 13 ms per card
    /// — so a refresh that arrives at the same page (a keystroke that narrows nothing, a page-size set to what it
    /// already was, an order re-applied) is worth not doing at all rather than doing invisibly.
    /// </summary>
    private bool IsAlreadyShowing(List<FitRowViewModel> page)
    {
        if (page.Count != PagedRows.Count) return false;
        for (var i = 0; i < page.Count; i++)
            if (!ReferenceEquals(page[i], PagedRows[i])) return false;
        return true;
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
