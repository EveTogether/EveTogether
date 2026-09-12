using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-215: the LOOT section, grouped by character — one block per run with its own subtotal, the group's total under
/// all of them. One component for both places loot is shown: the saved activity's detail screen and the run window,
/// so the two cannot drift into two looks of the same list. Each block is a <see cref="RunLootViewModel"/>, so a
/// correction made here runs through the very commands the run window always used — there is no second way to edit.
///
/// The group's figures are the blocks' own figures added up, never read from anywhere else: what is shown per
/// character and what is shown for the group are the same numbers by construction.
/// </summary>
public sealed partial class ActivityLootViewModel : ObservableObject
{
    private readonly Func<RunLootViewModel> _createLoot;
    private readonly ICharacterPortraitProvider? _portraits;

    public ActivityLootViewModel(Func<RunLootViewModel> createLoot, ICharacterPortraitProvider? portraits = null)
    {
        _createLoot = createLoot;
        _portraits = portraits;
        Characters.CollectionChanged += (_, _) => _RefreshFigures();
        FleetCharacters.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFleetCharacters));
            OnPropertyChanged(nameof(FleetSummaryText));
        };
    }

    public ObservableCollection<ActivityLootCharacterViewModel> Characters { get; } = [];

    /// <summary>
    /// What fleet members share live from their own runs (ET-242), one block per pilot, under the group's own. Only the
    /// run window fills it. Never part of any figure here: those are this run's own, the same set TOTAL ISK counts, and
    /// a member's run joins them the moment it is published to this client and read from the store instead.
    /// </summary>
    public ObservableCollection<ActivityLootCharacterViewModel> FleetCharacters { get; } = [];

    public bool HasFleetCharacters => FleetCharacters.Count > 0;

    public string FleetSummaryText => FleetCharacters.Count == 1
        ? "SHARED LIVE BY THE FLEET · 1 PILOT"
        : $"SHARED LIVE BY THE FLEET · {FleetCharacters.Count} PILOTS";

    public string FleetNoteText =>
        "What other pilots on this run share from their own, as it comes in, valued here the same way. Not in the totals "
        + "above: those count this run's own characters.";

    /// <summary>A correction made in one of the blocks landed. Raised after the block has already re-read itself, so
    /// whoever listens re-reads only what lies outside the section (a saved activity's TOTAL ISK and summary).</summary>
    public event Action? LootCorrected;

    public bool HasCharacters => Characters.Count > 0;

    /// <summary>Whether there are any figures to show at all. Without a single capture the totals would only read
    /// "no price" three times over; the blocks still stand, each saying it has nothing and offering the list by hand.</summary>
    public bool HasCaptures => Characters.Any(block => block.Loot.HasCaptures);

    public decimal? LootIsk { get; private set; }

    public decimal? ConsumedIsk { get; private set; }

    /// <summary>Null when no block has a priced figure — never 0 for "nothing priced" (ET-65 AC-5).</summary>
    public decimal? NetIsk { get; private set; }

    public string LootIskDisplay => _Display(LootIsk);

    public string ConsumedIskDisplay => _Display(ConsumedIsk);

    public string NetIskDisplay => _Display(NetIsk);

    /// <summary>"2 CHARACTERS · 9 ITEMS · 11 CAPTURES · 2 EXCLUDED" — what the section holds before any of it is
    /// opened, in the mockup's capitals. An item is a kind of item that counts, however many copies it came in.</summary>
    public string SummaryText
    {
        get
        {
            int characters = Characters.Count;
            int items = Characters.Sum(block => block.Loot.ItemRows.Count(row => !row.IsExcluded));
            int captures = Characters.Sum(block => block.Loot.Captures.Count);
            int excluded = Characters.Sum(block => block.Loot.ExcludedCount);
            return $"{characters} {(characters == 1 ? "CHARACTER" : "CHARACTERS")} · {items} {(items == 1 ? "ITEM" : "ITEMS")} · "
                   + $"{captures} {(captures == 1 ? "CAPTURE" : "CAPTURES")} · {excluded} EXCLUDED";
        }
    }

    /// <summary>What the figures are, in the mockup's own words under the totals.</summary>
    public string PricingText => "Valued per type id from the cached ESI average price, never from the copied ISK column.";

    /// <summary>Why there are no figures, when the price cache has nothing in it yet — said, not left to three
    /// "no price" lines.</summary>
    public string? PricingProblemText => Characters.Select(block => block.Loot.PricingProblemText)
        .FirstOrDefault(problem => problem is not null);

    /// <summary>Counted, not hidden: a row the lookup has no price for is not worth nothing, it is worth something
    /// nobody has told us (ET-159 AC-2).</summary>
    public string? LinesWithoutPriceText => Characters.Sum(block => block.Loot.EntriesWithoutPrice) switch
    {
        0 => null,
        1 => "1 line has no price in the cache and counts towards nothing.",
        var count => $"{count} lines have no price in the cache and count towards nothing."
    };

    /// <summary>The block for this run, made when it is not there yet and kept as it is when it is — its open captures
    /// and a list half-typed by hand survive the owner reading the runs again.</summary>
    public ActivityLootCharacterViewModel Show(Guid runId, long characterId, string characterName)
    {
        if (Characters.FirstOrDefault(block => block.RunId == runId) is { } existing)
            return existing;

        var block = new ActivityLootCharacterViewModel(runId, characterId, characterName, _createLoot());
        block.Loot.PropertyChanged += _OnBlockChanged;
        block.Loot.LootCorrected += () => LootCorrected?.Invoke();
        Characters.Add(block);
        if (_portraits is not null)
            _ = block.LoadPortraitAsync(_portraits);
        return block;
    }

    /// <summary>Drops every block whose run is no longer part of what is shown — a run discarded out of the group,
    /// or a window moving on to a new run.</summary>
    public void Keep(IReadOnlyCollection<Guid> runIds)
    {
        foreach (ActivityLootCharacterViewModel gone in Characters.Where(block => !runIds.Contains(block.RunId)).ToList())
        {
            gone.Loot.PropertyChanged -= _OnBlockChanged;
            Characters.Remove(gone);
        }
    }

    /// <summary>A fleet member's live share, in their block — made on first sight and reloaded in place after that, so
    /// the list does not jump while it is being read.</summary>
    public async Task ShowSharedAsync(long characterId, string characterName, IReadOnlyList<RunLootEntryDto> counted,
        int captureCount, DateTime sharedAtUtc)
    {
        if (FleetCharacters.FirstOrDefault(block => block.CharacterId == characterId) is not { } block)
        {
            block = new ActivityLootCharacterViewModel(Guid.Empty, characterId, characterName, _createLoot(), isSharedByFleet: true);
            FleetCharacters.Add(block);
            if (_portraits is not null)
                _ = block.LoadPortraitAsync(_portraits);
        }

        block.SharedCaptureCount = captureCount;
        await block.Loot.LoadSharedAsync(counted, sharedAtUtc);
    }

    /// <summary>Drops the live block of every pilot no longer sharing — switched off, gone, or now read from the
    /// store as a run of the group.</summary>
    public void KeepShared(IReadOnlyCollection<long> characterIds)
    {
        foreach (ActivityLootCharacterViewModel gone in FleetCharacters.Where(block => !characterIds.Contains(block.CharacterId)).ToList())
            FleetCharacters.Remove(gone);
    }

    /// <summary>Largest contribution first, the order BOUNTY and ENEMIES use. Done once by the owner when the blocks
    /// are first shown, never after a correction: a block moving away under the pointer is worse than an order that
    /// no longer matches the figures exactly.</summary>
    public void OrderByValue()
    {
        List<ActivityLootCharacterViewModel> ordered = [.. Characters
            .OrderByDescending(block => block.Loot.NetIsk.HasValue)
            .ThenByDescending(block => block.Loot.NetIsk)];
        for (int index = 0; index < ordered.Count; index++)
            Characters.Move(Characters.IndexOf(ordered[index]), index);
    }

    /// <summary>Re-reads one run's block from the store, when it is one of these.</summary>
    public Task RefreshRunAsync(Guid runId) =>
        Characters.FirstOrDefault(block => block.RunId == runId)?.Loot.RefreshAsync() ?? Task.CompletedTask;

    private void _OnBlockChanged(object? sender, PropertyChangedEventArgs e)
    {
        // One correction at a time across the whole activity: each one rebuilds the same summary.
        if (e.PropertyName is nameof(RunLootViewModel.IsBusy))
        {
            bool isAnyBusy = Characters.Any(block => block.Loot.IsBusy);
            foreach (ActivityLootCharacterViewModel block in Characters)
                block.Loot.IsHeld = isAnyBusy && !block.Loot.IsBusy;
            return;
        }

        if (e.PropertyName is nameof(RunLootViewModel.NetIsk) or nameof(RunLootViewModel.LootIsk)
            or nameof(RunLootViewModel.ConsumedIsk) or nameof(RunLootViewModel.EntriesWithoutPrice)
            or nameof(RunLootViewModel.ExcludedCount) or nameof(RunLootViewModel.HasCaptures)
            or nameof(RunLootViewModel.PricingProblemText))
            _RefreshFigures();
    }

    private void _RefreshFigures()
    {
        LootIsk = _SumKnown(Characters.Select(block => block.Loot.LootIsk));
        ConsumedIsk = _SumKnown(Characters.Select(block => block.Loot.ConsumedIsk));
        NetIsk = _SumKnown(Characters.Select(block => block.Loot.NetIsk));
        OnPropertyChanged(nameof(LootIsk));
        OnPropertyChanged(nameof(ConsumedIsk));
        OnPropertyChanged(nameof(NetIsk));
        OnPropertyChanged(nameof(LootIskDisplay));
        OnPropertyChanged(nameof(ConsumedIskDisplay));
        OnPropertyChanged(nameof(NetIskDisplay));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(PricingProblemText));
        OnPropertyChanged(nameof(LinesWithoutPriceText));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasCaptures));
    }

    private static decimal? _SumKnown(IEnumerable<decimal?> figures)
    {
        decimal[] known = [.. figures.Where(figure => figure.HasValue).Select(figure => figure.GetValueOrDefault())];
        return known.Length == 0 ? null : known.Sum();
    }

    private static string _Display(decimal? value) => IskFormat.WholeOrNoPrice(value);
}
