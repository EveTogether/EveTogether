using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Esi;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-65 phase 3: the running run's loot snapshots with their in/out switch, plus why there is nothing to show when
/// there isn't. Deliberately not a window — ET-98 phase 4 binds this into the activity window's LOOT section, so a
/// loot list is drawn once rather than twice.
/// </summary>
public sealed partial class RunLootViewModel : ViewModelBase
{
    private readonly CqrsDispatcher _dispatcher;

    public RunLootViewModel(CqrsDispatcher dispatcher) => _dispatcher = dispatcher;

    public ObservableCollection<RunLootCaptureRowViewModel> Captures { get; } = [];

    /// <summary>Null when at least one priced entry is included — never 0 for "no priced loot" (ET-65 AC-5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalIskDisplay))]
    private decimal? _totalIsk;

    [ObservableProperty] private int _entriesWithoutPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LootIskDisplay))]
    private decimal? _lootIsk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsumedIskDisplay))]
    private decimal? _consumedIsk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetIskDisplay))]
    private decimal? _netIsk;

    /// <summary>Set when the running-run lookup itself failed (none running, or more than one) — a state, not an
    /// empty list left to speak for itself (ET-65 AC-7).</summary>
    [ObservableProperty] private string? _runStatusMessage;

    /// <summary>Set from <see cref="ApplyLocationState"/> when location tracking itself explains why there is no
    /// run to attach loot to right now.</summary>
    [ObservableProperty] private string? _locationStatusMessage;

    /// <summary>The label this total is shown under — asserted to never say Jita/market/waardering/appraisal
    /// (ET-65 AC-5): it is the clipboard's own ISK column, not a valuation.</summary>
    public string TotalIskLabel => "Clipboard ISK total";

    public string EntriesWithoutPriceLabel => "Rows without a price";

    public string TotalIskDisplay => TotalIsk is { } value ? $"{value:N2} ISK" : "no price";

    public string LootIskDisplay => _Display(LootIsk);

    public string ConsumedIskDisplay => _Display(ConsumedIsk);

    public string NetIskDisplay => _Display(NetIsk);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Result<RunLootOverview> overview = await _dispatcher.Query(new GetRunningRunLootQuery(), cancellationToken);
        Captures.Clear();
        if (!overview.IsSuccess)
        {
            RunStatusMessage = overview.Messages.Count > 0 ? overview.Messages[0].Text : "No running run.";
            _Recompute();
            return;
        }

        RunStatusMessage = null;
        var firstSeenAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (RunLootCaptureDto capture in overview.Value!.Captures)
        {
            DateTime? repeatOf = null;
            if (capture.ContentHash is { } hash)
            {
                if (firstSeenAt.TryGetValue(hash, out var earlier))
                    repeatOf = earlier;
                else
                    firstSeenAt[hash] = capture.CapturedAtUtc;
            }

            Captures.Add(new RunLootCaptureRowViewModel(capture, repeatOf));
        }

        _Recompute();
    }

    /// <summary>The one click AC-6 asks for: excludes or re-includes a capture and updates the total to match.
    /// Never removes the row — exclusion is a flag, not a deletion.</summary>
    public async Task<bool> ToggleExcludedAsync(RunLootCaptureRowViewModel row, CancellationToken cancellationToken = default)
    {
        var isExcluded = !row.IsExcluded;
        Result result = await _dispatcher.Send(new SetRunLootCaptureExclusionCommand(row.CaptureId, isExcluded), cancellationToken);
        if (!result.IsSuccess)
            return false;

        row.IsExcluded = isExcluded;
        _Recompute();
        return true;
    }

    [RelayCommand]
    private Task ToggleCaptureExcludedAsync(RunLootCaptureRowViewModel row) => ToggleExcludedAsync(row);

    /// <summary>Four states, three of them a reason rather than silence (ET-65 AC-7): watching off, an anchor
    /// present (nothing to explain), an anchor lost with a known reason, and an anchor never set (e.g. a restart —
    /// C6 point 2, no reason to report). Reuses <see cref="CharacterMetricsSnapshot.LocationUnavailableReason"/>
    /// rather than inventing a second story.</summary>
    public void ApplyLocationState(DateTime? abyssalAnchor, EsiErrorKind? locationUnavailableReason, bool clipboardWatching)
    {
        LocationStatusMessage = !clipboardWatching
            ? "The clipboard watch is off, so a copied inventory would not be seen."
            : abyssalAnchor is not null
                ? null
                : locationUnavailableReason is { } reason
                    ? $"Location tracking lost the abyssal anchor: {EsiLocationReasonText.Describe(reason)}."
                    : "No abyssal anchor yet — location tracking has not confirmed a run since the app started.";
    }

    private void _Recompute()
    {
        List<RunLootEntryDto> included = [.. Captures.Where(capture => !capture.IsExcluded).SelectMany(capture => capture.Entries)];
        decimal[] priced = [.. included.Where(entry => entry.ClipboardPrice is not null).Select(entry => entry.ClipboardPrice!.Value)];
        TotalIsk = priced.Length == 0 ? null : priced.Sum();
        EntriesWithoutPrice = included.Count(entry => entry.ClipboardPrice is null);
        LootIsk = _Sum(included.Where(entry => entry.LootKind == LootKind.Gained));
        ConsumedIsk = _Sum(included.Where(entry => entry.LootKind == LootKind.Lost));
        NetIsk = LootIsk is null && ConsumedIsk is null ? null : (LootIsk ?? 0m) - (ConsumedIsk ?? 0m);
    }

    private static decimal? _Sum(IEnumerable<RunLootEntryDto> entries)
    {
        decimal[] prices = [.. entries.Where(entry => entry.ClipboardPrice is not null).Select(entry => entry.ClipboardPrice!.Value)];
        return prices.Length == 0 ? null : prices.Sum();
    }

    private static string _Display(decimal? value) => value is { } isk ? $"{isk:N2} ISK" : "no price";
}
