using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The loot table window (ET-519): what can drop in one activity, with the drop rate, the quantity, the worth and the share
/// of the loot value of each item, and the rare ones folded into a group of their own. One instance, opened on a key and
/// switched to another by a second click; the sort and the open or closed rare group stay put across the switch. Opened
/// without a key it offers the activities the pilot has flown, searched the way the runs overview is (ET-518).
///
/// <para>What a number may say is decided in <see cref="LootTableStatistics"/>; this only words it. Below the minimum of
/// runs there is no table, only the names of what was seen; from 5 runs it is "seen in 9 of 12"; from 20, percentages.
/// A server that cannot answer, or has too few pilots on the activity, leaves the pilot's own runs on screen with the
/// reason beside them, never a toast.</para>
/// </summary>
public sealed partial class LootTableViewModel(ILootTableSource source, ActivityStatsLabels labels, ISdeAccessor? sde = null,
    ITypeImageProvider? images = null, TimeProvider? time = null) : ViewModelBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<int> _characterIds = [];
    private IReadOnlyList<LootSiteChoice> _flown = [];
    private IReadOnlyList<LootSiteChoiceViewModel> _choices = [];
    private CancellationTokenSource? _reading;
    private LootTableReading? _last;
    private ActivityStatsKey? _key;

    [ObservableProperty] private LootTableStage _stage = LootTableStage.Picking;
    [ObservableProperty] private bool _isAllPilots = true;
    [ObservableProperty] private LootTableWindow _window = LootTableWindow.Days90;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private LootTableSort _sort = LootTableSort.Share;
    [ObservableProperty] private bool _isDescending = true;
    [ObservableProperty] private bool _isRareOpen;
    [ObservableProperty] private string _kindText = string.Empty;
    [ObservableProperty] private string _nameText = string.Empty;
    [ObservableProperty] private string _familyText = string.Empty;
    [ObservableProperty] private string _sourceChip = string.Empty;
    [ObservableProperty] private bool _isSharedSource;
    [ObservableProperty] private string _noteText = string.Empty;
    [ObservableProperty] private bool _hasTable;
    [ObservableProperty] private bool _hasSeenNames;
    [ObservableProperty] private string _tooFewText = string.Empty;
    [ObservableProperty] private string _expectedText = string.Empty;
    [ObservableProperty] private string _typicalText = string.Empty;
    [ObservableProperty] private string _fixedText = string.Empty;
    [ObservableProperty] private string _fewRunsText = string.Empty;
    [ObservableProperty] private string _changedText = string.Empty;
    [ObservableProperty] private bool _hasRare;
    [ObservableProperty] private string _rareTitleText = string.Empty;
    [ObservableProperty] private string _rareDetailText = string.Empty;
    [ObservableProperty] private string _rareFigureText = string.Empty;
    [ObservableProperty] private string _basedOnText = string.Empty;
    [ObservableProperty] private string _leftOutText = string.Empty;
    [ObservableProperty] private string _unpricedText = string.Empty;
    [ObservableProperty] private bool _hasSummaryFigures;
    [ObservableProperty] private bool _isHowOpen;

    public bool IsPicking => Stage is LootTableStage.Picking;

    public bool IsLoading => Stage is LootTableStage.Loading;

    public bool ShowsTable => Stage is LootTableStage.Table;

    public bool HasNote => NoteText.Length > 0;

    public bool HasFixed => FixedText.Length > 0;

    public bool HasFewRunsText => FewRunsText.Length > 0;

    public bool HasChangedText => ChangedText.Length > 0;

    public bool HasLeftOut => LeftOutText.Length > 0;

    public bool HasUnpriced => UnpricedText.Length > 0;

    public bool IsYourRuns => !IsAllPilots;

    public bool IsDays30 => Window is LootTableWindow.Days30;

    public bool IsDays90 => Window is LootTableWindow.Days90;

    public bool IsSinceUpdate => Window is LootTableWindow.SinceUpdate;

    /// <summary>The game update the SINCE UPDATE switch is dated from; without an SDE store there is none and the switch is not offered.</summary>
    public bool HasUpdate => sde?.Version is not null;

    public string UpdateLabel => sde?.Version is { } version
        ? $"{version.ReleaseDate.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture).ToUpperInvariant()}"
        : string.Empty;

    public string SinceUpdateText => $"SINCE {UpdateLabel} UPDATE";

    public string RareArrow => IsRareOpen ? "▼" : "▶";

    public string HowToggleText => IsHowOpen ? "How is this calculated? ▾" : "How is this calculated? ›";

    public string NameMark => _MarkOf(LootTableSort.Name);

    public string RateMark => _MarkOf(LootTableSort.Rate);

    public string QuantityMark => _MarkOf(LootTableSort.Quantity);

    public string ValueMark => _MarkOf(LootTableSort.Value);

    public string ShareMark => _MarkOf(LootTableSort.Share);

    public ActivityStatsKey? Key => _key;

    /// <summary>One dot per run needed for a table, filled for each run there is.</summary>
    public ObservableCollection<bool> RunDots { get; } = [];

    /// <summary>Everything seen so far, by name only: what the window says below the minimum of runs.</summary>
    public ObservableCollection<LootTableRowViewModel> SeenNames { get; } = [];

    public ObservableCollection<LootTableRowViewModel> Items { get; } = [];

    public ObservableCollection<LootTableRowViewModel> RareItems { get; } = [];

    public ObservableCollection<LootSiteChoiceViewModel> Choices { get; } = [];

    public bool HasNoChoices => Choices.Count == 0 && Stage is LootTableStage.Picking;

    public string NoChoicesText => _choices.Count == 0
        ? "No activity flown yet. Loot tables list the sites you have flown once you have."
        : "No site matches.";

    /// <summary>Opens the window on <paramref name="key"/>, or with none on the list of activities the pilot has flown. A
    /// second call with another key only swaps the key.</summary>
    public async Task ShowAsync(ActivityStatsKey? key, IReadOnlyList<int> characterIds)
    {
        _characterIds = characterIds;
        _reading?.Cancel();
        _key = key;
        OnPropertyChanged(nameof(Key));
        if (key is null)
        {
            Stage = LootTableStage.Picking;
            await _LoadChoicesAsync();
            return;
        }

        await _ReadAsync();
    }

    partial void OnStageChanged(LootTableStage value)
    {
        OnPropertyChanged(nameof(IsPicking));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowsTable));
        OnPropertyChanged(nameof(HasNoChoices));
    }

    partial void OnSearchTextChanged(string value) => _ApplyChoiceFilter();

    partial void OnNoteTextChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnFixedTextChanged(string value) => OnPropertyChanged(nameof(HasFixed));

    partial void OnFewRunsTextChanged(string value) => OnPropertyChanged(nameof(HasFewRunsText));

    partial void OnChangedTextChanged(string value) => OnPropertyChanged(nameof(HasChangedText));

    partial void OnLeftOutTextChanged(string value) => OnPropertyChanged(nameof(HasLeftOut));

    partial void OnUnpricedTextChanged(string value) => OnPropertyChanged(nameof(HasUnpriced));

    partial void OnIsAllPilotsChanged(bool value) => OnPropertyChanged(nameof(IsYourRuns));

    partial void OnWindowChanged(LootTableWindow value)
    {
        OnPropertyChanged(nameof(IsDays30));
        OnPropertyChanged(nameof(IsDays90));
        OnPropertyChanged(nameof(IsSinceUpdate));
    }

    partial void OnIsRareOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(RareArrow));
        _RenderRare();
    }

    partial void OnIsHowOpenChanged(bool value) => OnPropertyChanged(nameof(HowToggleText));

    [RelayCommand]
    private Task ShowAllPilotsAsync() => _SetSourceAsync(true);

    [RelayCommand]
    private Task ShowYourRunsAsync() => _SetSourceAsync(false);

    [RelayCommand]
    private Task Show30DaysAsync() => _SetWindowAsync(LootTableWindow.Days30);

    [RelayCommand]
    private Task Show90DaysAsync() => _SetWindowAsync(LootTableWindow.Days90);

    [RelayCommand]
    private Task ShowSinceUpdateAsync() => _SetWindowAsync(LootTableWindow.SinceUpdate);

    [RelayCommand]
    private Task SwitchSiteAsync() => ShowAsync(null, _characterIds);

    [RelayCommand]
    private void ToggleRare() => IsRareOpen = !IsRareOpen;

    [RelayCommand]
    private void ToggleHow() => IsHowOpen = !IsHowOpen;

    [RelayCommand]
    private void SortByName() => _SortBy(LootTableSort.Name);

    [RelayCommand]
    private void SortByRate() => _SortBy(LootTableSort.Rate);

    [RelayCommand]
    private void SortByQuantity() => _SortBy(LootTableSort.Quantity);

    [RelayCommand]
    private void SortByValue() => _SortBy(LootTableSort.Value);

    [RelayCommand]
    private void SortByShare() => _SortBy(LootTableSort.Share);

    private Task _SetSourceAsync(bool allPilots)
    {
        if (IsAllPilots == allPilots)
            return Task.CompletedTask;

        IsAllPilots = allPilots;
        return _ReadAsync();
    }

    private Task _SetWindowAsync(LootTableWindow window)
    {
        if (Window == window)
            return Task.CompletedTask;

        Window = window;
        return _ReadAsync();
    }

    private void _SortBy(LootTableSort column)
    {
        IsDescending = Sort == column ? !IsDescending : column is not LootTableSort.Name;
        Sort = column;
        foreach (string mark in new[] { nameof(NameMark), nameof(RateMark), nameof(QuantityMark), nameof(ValueMark), nameof(ShareMark) })
            OnPropertyChanged(mark);

        if (_last is { } last)
            _Render(last);
    }

    private string _MarkOf(LootTableSort column) => Sort == column ? (IsDescending ? " ▼" : " ▲") : string.Empty;

    private async Task _ReadAsync()
    {
        if (_key is not { } key)
            return;

        _reading?.Cancel();
        _reading?.Dispose();
        var reading = _reading = new CancellationTokenSource();
        Stage = LootTableStage.Loading;
        try
        {
            var request = new LootTableRequest(key, IsAllPilots ? EarningsBasis.AllPilots : EarningsBasis.YourRuns, Window, _characterIds);
            Task<IReadOnlyList<LootSiteChoice>> flown = source.ListFlownAsync(reading.Token);
            LootTableReading read = await Task.Run(() => source.ReadAsync(request, reading.Token), reading.Token);
            _flown = await flown;
            if (reading.IsCancellationRequested)
                return;

            _last = read;
            _Render(read);
            Stage = LootTableStage.Table;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void _Render(LootTableReading read)
    {
        LootTableDto table = read.Table;
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        ActivityStatsKey key = table.Key;
        string? siteName = _flown.FirstOrDefault(activity => activity.Key == key)?.SiteName;
        ActivityStatsLabel label = labels.For(key, siteName);
        KindText = $"LOOT TABLE · {label.TypeText.ToUpperInvariant()}";
        NameText = label.NameText;
        FamilyText = label.FamilyText;
        IsSharedSource = read.Basis is EarningsBasis.AllPilots;
        SourceChip = IsSharedSource ? $"ALL PILOTS · {read.ServerName}" : "YOUR RUNS";
        NoteText = read.Basis is EarningsBasis.YourRuns && IsAllPilots ? OwnRunsReasonText.For(read.Reason) : string.Empty;

        LootTableRowViewModel[] rows = [.. table.Items.Select(item => new LootTableRowViewModel(item, _NameOf(item.TypeId), table.Confidence,
            table.RunCount, nowUtc))];
        _LoadIcons(rows);
        HasTable = table.Confidence is not LootTableConfidence.None;
        HasSeenNames = !HasTable && rows.Length > 0;
        _RenderTooFew(table, rows);
        _RenderSummary(table);
        _RenderBanners(table);
        _RenderFooter(table);

        Items.Clear();
        if (HasTable)
        {
            foreach (LootTableRowViewModel row in _Sorted(rows.Where(row => !row.IsRare)))
                Items.Add(row);
        }

        _rareRows = [.. _Sorted(rows.Where(row => row.IsRare))];
        HasRare = HasTable && _rareRows.Count > 0;
        RareTitleText = "RARE DROPS";
        RareDetailText = $"{table.RareKindCount.ToString(CultureInfo.InvariantCulture)} kinds · faction, storyline, blueprints"
                         + (table.Confidence is LootTableConfidence.Rated ? " and anything seen in under 5 % of runs" : string.Empty);
        RareFigureText = table.Confidence is LootTableConfidence.Rated
            ? $"seen in {LootTableRowViewModel.Percentage(table.RareRunShare)} of runs · {LootTableRowViewModel.Percentage(table.RareValueShare)} of value"
            : $"seen in {table.RareRunCount.ToString(CultureInfo.InvariantCulture)} of {table.RunCount.ToString(CultureInfo.InvariantCulture)} runs";
        _RenderRare();
    }

    private IReadOnlyList<LootTableRowViewModel> _rareRows = [];

    private void _RenderRare()
    {
        RareItems.Clear();
        if (!IsRareOpen)
            return;

        foreach (LootTableRowViewModel row in _rareRows)
            RareItems.Add(row);
    }

    private void _RenderTooFew(LootTableDto table, IReadOnlyList<LootTableRowViewModel> rows)
    {
        RunDots.Clear();
        SeenNames.Clear();
        if (HasTable)
        {
            TooFewText = string.Empty;
            return;
        }

        int needed = (int)LootTablePolicy.Default.MinimumRuns;
        int have = Math.Min(needed, (int)Math.Floor(table.EffectiveRunCount));
        for (int dot = 0; dot < needed; dot++)
            RunDots.Add(dot < have);

        TooFewText = $"Not enough runs yet — {have.ToString(CultureInfo.InvariantCulture)} of {needed.ToString(CultureInfo.InvariantCulture)} needed";
        foreach (LootTableRowViewModel row in rows.OrderBy(row => row.NameText, StringComparer.OrdinalIgnoreCase))
            SeenNames.Add(row);
    }

    private void _RenderSummary(LootTableDto table)
    {
        HasSummaryFigures = HasTable && table.ExpectedLoot is not null;
        ExpectedText = table.ExpectedLoot is { } expected ? IskFormat.Compact(expected) : string.Empty;
        TypicalText = table.TypicalLoot is { } typical ? IskFormat.Compact(typical) : string.Empty;
        FixedText = table.ExpectedFixedIsk is > 0 and { } fixedIsk ? $"+{IskFormat.Compact(fixedIsk)}" : string.Empty;
    }

    private void _RenderBanners(LootTableDto table)
    {
        FewRunsText = table.Confidence is LootTableConfidence.Seen
            ? $"few runs · n={table.RunCount.ToString(CultureInfo.InvariantCulture)}  Percentages need 20 runs. Items are shown as “seen in X of {table.RunCount.ToString(CultureInfo.InvariantCulture)} runs”; the order may change as runs come in."
            : string.Empty;
        int changed = table.Items.Count(item => item.Change is not LootItemChange.None);
        ChangedText = table.HasUpdateFlags && changed > 0
            ? $"game data updated {UpdateLabel}  {changed.ToString(CultureInfo.InvariantCulture)} {(changed == 1 ? "item has" : "items have")} changed since."
            : string.Empty;
    }

    private void _RenderFooter(LootTableDto table)
    {
        string span = table.Window switch
        {
            LootTableWindow.Days30 => "last 30 days",
            LootTableWindow.SinceUpdate => $"since {UpdateLabel}",
            _ => "last 90 days"
        };
        BasedOnText = $"based on {table.RunCount.ToString(CultureInfo.InvariantCulture)} runs · {table.PilotCount.ToString(CultureInfo.InvariantCulture)} "
                      + $"{(table.PilotCount == 1 ? "pilot" : "pilots")} · {span} · priced at today's ESI average";
        List<string> leftOut = [];
        if (table.UncountedRunCount > 0)
            leftOut.Add($"{table.UncountedRunCount.ToString(CultureInfo.InvariantCulture)} {(table.UncountedRunCount == 1 ? "run" : "runs")} without a loot count");
        if (table.FleetRunCount > 0)
            leftOut.Add($"{table.FleetRunCount.ToString(CultureInfo.InvariantCulture)} fleet {(table.FleetRunCount == 1 ? "run" : "runs")}");
        LeftOutText = leftOut.Count == 0
            ? string.Empty
            : $"Left out: {string.Join(" and ", leftOut)}. A run whose loot was not counted between a starting and an ending hold is not a run with nothing, and a fleet run holds one pilot's loot only.";
        UnpricedText = table.UnpricedItemCount > 0
            ? $"{table.UnpricedItemCount.ToString(CultureInfo.InvariantCulture)} {(table.UnpricedItemCount == 1 ? "item has" : "items have")} no price and {(table.UnpricedItemCount == 1 ? "counts" : "count")} as 0."
            : string.Empty;
    }

    private IEnumerable<LootTableRowViewModel> _Sorted(IEnumerable<LootTableRowViewModel> rows)
    {
        Func<LootTableRowViewModel, IComparable> key = Sort switch
        {
            LootTableSort.Name => row => row.NameText.ToUpperInvariant(),
            LootTableSort.Rate => row => row.Item.DropRate,
            LootTableSort.Quantity => row => row.Item.AverageQuantity,
            LootTableSort.Value => row => row.Item.ValuePerDrop,
            _ => row => row.Item.Contribution
        };
        return IsDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
    }

    private string _NameOf(int typeId) =>
        sde is not null && sde.TryGetTypeName(typeId, out string name) ? name : $"Type {typeId.ToString(CultureInfo.InvariantCulture)}";

    private void _LoadIcons(IEnumerable<LootTableRowViewModel> rows)
    {
        if (images is null)
            return;

        foreach (LootTableRowViewModel row in rows)
            _ = row.LoadIconAsync(images);
    }

    private async Task _LoadChoicesAsync()
    {
        IReadOnlyList<LootSiteChoice> flown = await source.ListFlownAsync();
        _flown = flown;
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        _choices = [.. flown.Select(choice => new LootSiteChoiceViewModel(choice, labels.For(choice.Key, choice.SiteName), nowUtc,
            key => _ = ShowAsync(key, _characterIds)))];
        _ApplyChoiceFilter();
    }

    private void _ApplyChoiceFilter()
    {
        IReadOnlyList<string> words = ActivitySearchTerms.Words(SearchText);
        Choices.Clear();
        foreach (LootSiteChoiceViewModel choice in _choices.Where(choice => ActivitySearchTerms.Matches(choice.SearchText, words)))
            Choices.Add(choice);

        OnPropertyChanged(nameof(HasNoChoices));
        OnPropertyChanged(nameof(NoChoicesText));
    }
}
