using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Esi;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Runs.Tally;
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
    private readonly IAppraisalProvider? _appraisal;
    private readonly Dictionary<int, decimal> _unitPrices = [];
    private string? _pricingBasis;

    public RunLootViewModel(CqrsDispatcher dispatcher, IAppraisalProvider? appraisal = null)
    {
        _dispatcher = dispatcher;
        _appraisal = appraisal;
    }

    public ObservableCollection<RunLootCaptureRowViewModel> Captures { get; } = [];

    /// <summary>The run whose loot this section shows — set by the window that owns it, which has known the id all
    /// along. This used to ask "which run is running" instead, so the section read the store's guess rather than
    /// its own run: with eleven runs stopped and never saved that guess is ambiguous forever and the section stayed
    /// empty behind "11 runs are running" (Raymond, 2026-09-04). Null is a window with no run yet, which is a
    /// state to say out loud rather than a question to ask the store (ET-65 AC-7).</summary>
    public Guid? RunId { get; set; }

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

    /// <summary>What these figures are, said by whoever priced them. ET-65 AC-5 had this read "Clipboard ISK total"
    /// because the total WAS the copied column; it is a valuation now, so the label says so — including when the
    /// price cache has nothing in it yet and the honest answer is why there are no figures.</summary>
    public string TotalIskLabel => _pricingBasis ?? "Valued at the cached ESI average price per item.";

    public string EntriesWithoutPriceLabel => "Rows without a price";

    public string TotalIskDisplay => TotalIsk is { } value ? $"{value:N2} ISK" : "no price";

    public string LootIskDisplay => _Display(LootIsk);

    public string ConsumedIskDisplay => _Display(ConsumedIsk);

    public string NetIskDisplay => _Display(NetIsk);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (RunId is not { } runId)
        {
            Captures.Clear();
            RunStatusMessage = "No run yet, so there is no loot to show.";
            _Recompute();
            return;
        }

        Result<RunLootOverview> overview = await _dispatcher.Query(new GetRunLootQuery(runId), cancellationToken);
        Captures.Clear();
        if (!overview.IsSuccess)
        {
            RunStatusMessage = overview.Messages.Count > 0 ? overview.Messages[0].Text : "No running run.";
            _Recompute();
            return;
        }

        RunStatusMessage = null;
        await _LoadPricesAsync(overview.Value!.Captures.SelectMany(capture => capture.Entries), cancellationToken);
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

    /// <summary>
    /// Prices every entry from <see cref="IAppraisalProvider"/> — the hourly ESI cache — by type id, and never from
    /// the clipboard's own ISK column (Raymond, 2026-09-02). That makes an Icons copy worth the same as the Details
    /// copy of the same items: the columns differ, the type ids do not.
    ///
    /// Prices are read once per refresh and held; excluding a capture re-totals from what is already here rather
    /// than asking again. A cache with nothing in it comes back as a failure, which becomes the label under the
    /// figures instead of a silent zero.
    /// </summary>
    private async Task _LoadPricesAsync(IEnumerable<RunLootEntryDto> entries, CancellationToken cancellationToken)
    {
        _unitPrices.Clear();
        _pricingBasis = null;
        if (_appraisal is null)
            return;

        List<AppraisalLine> lines = [.. entries
            .Select(entry => entry.ItemTypeId)
            .Distinct()
            .Where(typeId => typeId > 0)
            .Select(typeId => new AppraisalLine(typeId, string.Empty, 1))];
        if (lines.Count == 0)
            return;

        Result<AppraisalOutcome> valued = await _appraisal.AppraiseAsync(lines, cancellationToken);
        if (!valued.IsSuccess)
        {
            _pricingBasis = valued.Messages.Count > 0 ? valued.Messages[0].Text : null;
            return;
        }

        _pricingBasis = valued.Value!.PricingBasis;
        foreach (AppraisalRow row in valued.Value.Rows.Where(row => row.Price is not null))
            _unitPrices[row.Line.TypeId] = (decimal)row.Price!.Estimate;
    }

    /// <summary>Which captures count, and for how much, is <see cref="LootTally"/>'s answer and not this window's —
    /// the stored run is rebuilt from the same rule, so the figures here are the figures it will keep.</summary>
    private void _Recompute()
    {
        // No volume: a capture row carries none, and nothing on this screen totals one.
        IReadOnlyList<LootTallyLine> counted = LootTally.Count([.. Captures.Select(capture =>
            new LootTallyCapture(capture.Role, capture.IsExcluded, [.. capture.Entries.Select(entry =>
                new LootTallyLine(entry.ItemTypeId, entry.Quantity, null, entry.LootKind))]))]);
        TotalIsk = _Sum(counted);
        EntriesWithoutPrice = counted.Count(line => !_unitPrices.ContainsKey(line.ItemTypeId));
        LootIsk = _Sum(counted.Where(line => line.LootKind == LootKind.Gained));
        ConsumedIsk = _Sum(counted.Where(line => line.LootKind == LootKind.Lost));
        NetIsk = LootIsk is null && ConsumedIsk is null ? null : (LootIsk ?? 0m) - (ConsumedIsk ?? 0m);
        OnPropertyChanged(nameof(TotalIskLabel));
    }

    /// <summary>A market price is per unit, so the quantity is what turns it into a line value. No quantity column
    /// means one of it — the same reading <c>SdeInventoryResolver</c> takes.</summary>
    private decimal? _Sum(IEnumerable<LootTallyLine> lines)
    {
        decimal[] values = [.. lines
            .Where(line => _unitPrices.ContainsKey(line.ItemTypeId))
            .Select(line => _unitPrices[line.ItemTypeId] * (line.Quantity ?? 1))];
        return values.Length == 0 ? null : values.Sum();
    }

    private static string _Display(decimal? value) => value is { } isk ? $"{isk:N2} ISK" : "no price";
}
