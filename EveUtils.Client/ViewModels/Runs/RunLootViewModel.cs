using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Esi;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Sde;
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
/// ET-65 phase 3: the running run's loot — the rows that count as one list, the captures they came from as a strip
/// under it, and why there is nothing to show when there isn't. Deliberately not a window — ET-98 phase 4 binds this
/// into the activity window's LOOT section, so a loot list is drawn once rather than twice.
/// </summary>
public sealed partial class RunLootViewModel : ViewModelBase
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IAppraisalProvider? _appraisal;
    private readonly ISdeAccessor? _sde;
    private readonly Dictionary<int, decimal> _unitPrices = [];
    private readonly Dictionary<int, string> _names = [];
    private IReadOnlyList<LootTallyLine> _counted = [];
    private string? _pricingBasis;

    public RunLootViewModel(CqrsDispatcher dispatcher, IAppraisalProvider? appraisal = null, ISdeAccessor? sde = null)
    {
        _dispatcher = dispatcher;
        _appraisal = appraisal;
        _sde = sde;
    }

    public ObservableCollection<RunLootCaptureRowViewModel> Captures { get; } = [];

    /// <summary>The loot as it counts right now, whichever way it was registered: the captures added up, or the
    /// difference between two cargo holds. One list rather than a stack per capture, and the same
    /// <see cref="LootTally"/> answer the totals are made of — so a row a pilot can see is a row that counts.
    /// </summary>
    public ObservableCollection<ActivityLootLineViewModel> CountedLines { get; } = [];

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

    /// <summary>Whether this section offers the two paste boxes and the starting-hold picker. A window preference
    /// and only that: what counts is decided by the roles on the captures, which is why switching back to the
    /// clipboard way takes the controls off screen and never moves a figure (Zyra, 2026-09-04).</summary>
    [ObservableProperty] private bool _isCargoDiffShown;

    /// <summary>The run is saved, so nothing here is adjustable any more — by SAVE, or by ET-179 finishing a run
    /// left standing a day after STOP.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditableUntilText))]
    [NotifyPropertyChangedFor(nameof(CanEditLoot))]
    private bool _isLocked;

    /// <summary>An editable section says until when; a fixed one says it is fixed. Never nothing.</summary>
    public string EditableUntilText => IsLocked
        ? "This run is saved, so its loot is fixed."
        : "Pasting, editing the list and moving the starting hold all stay possible until this run is saved.";

    /// <summary>What the two paste boxes now hold, as the pilot left them. Writing back what was already stored is
    /// the text-correction ticket's job; this box is where the text goes in.</summary>
    [ObservableProperty] private string? _cargoBeforeText;

    [ObservableProperty] private string? _cargoAfterText;

    /// <summary>How many rows came out of that box, or why none did. The two refusals are the clipboard watch's own
    /// words — in a box they belong beside the text they turn down, not in a toast.</summary>
    [ObservableProperty] private string? _cargoBeforeStatus;

    [ObservableProperty] private string? _cargoAfterStatus;

    /// <summary>Which two captures the figures are the difference between, from <see cref="LootTally.Ends"/> rather
    /// than a second reading of the same roles. Shown whether or not the paste boxes are: someone back on the
    /// clipboard way who sees an unexpected total has to be able to read why in one glance.</summary>
    public string DifferenceText
    {
        get
        {
            (int before, int after) = LootTally.Ends(_TallyCaptures());
            return before < 0
                ? "no starting hold — every capture counts"
                : after < 0
                    ? $"starting hold #{Captures[before].Number}, nothing after it yet"
                    : $"difference #{Captures[before].Number} → #{Captures[after].Number}";
        }
    }

    /// <summary>Which capture the run started from — one place to say it, so a second starting hold is not something
    /// to catch. The guarantee is <see cref="SetRunLootCaptureRoleCommand"/>'s, which takes the role off whoever had
    /// it in the same write; this picker is only the shape it is asked in.</summary>
    public RunLootCaptureRowViewModel? CargoBeforeCapture
    {
        get => Captures.FirstOrDefault(capture => capture.IsCargoBefore);
        set
        {
            if (value is { IsCargoBefore: false })
                _TrackCargoWrite(MakeCargoBeforeAsync(value));
        }
    }

    /// <summary>The write a text box's change callback cannot wait for, so a test can. Chained rather than replaced,
    /// the same way <c>ClipboardLootCapture.LastStore</c> is: pasting into the second box while the first is still
    /// settling must not lose the first one's completion or its exception.</summary>
    internal Task LastCargoWrite { get; private set; } = Task.CompletedTask;

    /// <summary>What these figures are, said by whoever priced them. ET-65 AC-5 had this read "Clipboard ISK total"
    /// because the total WAS the copied column; it is a valuation now, so the label says so — including when the
    /// price cache has nothing in it yet and the honest answer is why there are no figures.</summary>
    public string TotalIskLabel => _pricingBasis ?? "Valued at the cached ESI average price per item.";

    public string EntriesWithoutPriceLabel => "Rows without a price";

    public string TotalIskDisplay => TotalIsk is { } value ? $"{value:N2} ISK" : "no price";

    public string LootIskDisplay => _Display(LootIsk);

    public string ConsumedIskDisplay => _Display(ConsumedIsk);

    public string NetIskDisplay => _Display(NetIsk);

    // ── The list as text ────────────────────────────────────────────────────────────────────────────

    /// <summary>The list is open for editing. While it is, the box is the list: what is in it is what "done" will
    /// make the loot.</summary>
    [ObservableProperty] private bool _isEditingLoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinishLootEdit))]
    private string? _lootText;

    /// <summary>Why the box cannot be accepted as it stands, in the clipboard watch's own words. Beside the text it
    /// turns down rather than in a toast, and it is the reason "done" is greyed: silently dropping a row the pilot
    /// typed is the one outcome worth blocking on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinishLootEdit))]
    private string? _lootTextRefusal;

    public bool CanFinishLootEdit => LootTextRefusal is null && !string.IsNullOrWhiteSpace(LootText);

    /// <summary>Correcting the list by hand belongs to the way that has no starting hold. With one, the list is the
    /// difference between two cargo holds — so the thing to correct is those two, in the boxes above, and a
    /// hand-written list would only be a third answer to a question that already has one.</summary>
    public bool CanEditLoot => !IsLocked && _sde is not null && LootTally.Ends(_TallyCaptures()).Before < 0;

    /// <summary>Set once the pilot has written the list out himself: the list is no longer what the app noticed, and
    /// says since when.</summary>
    public string? ManualListCaption =>
        Captures.LastOrDefault(capture => capture.Source is LootCaptureSource.Manual) is { } manual
            ? $"BY HAND · {manual.CapturedAtUtc.ToLocalTime():HH:mm}"
            : null;

    /// <summary>A capture that arrived after the pilot wrote his list wins and lands under it — loot that came in and
    /// does not count is the one mistake he would never see. Said beside the totals that just moved, on the strip
    /// row, and not in a toast.</summary>
    public string? AddedAfterEditNote
    {
        get
        {
            RunLootCaptureRowViewModel[] added = [.. Captures.Where(capture => capture.IsAddedAfterEdit)];
            return added.Length == 0
                ? null
                : $"{(added.Length == 1 ? "capture" : "captures")} "
                  + $"{string.Join(", ", added.Select(capture => capture.NumberDisplay))} added below · "
                  + $"{added.Sum(capture => capture.Entries.Count)} row(s)";
        }
    }

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
        IReadOnlyList<RunLootCaptureDto> captures = overview.Value!.Captures;
        await _LoadPricesAsync(captures.SelectMany(capture => capture.Entries), cancellationToken);
        _names.Clear();
        foreach (RunLootEntryDto entry in captures.SelectMany(capture => capture.Entries))
            _names[entry.ItemTypeId] = entry.Name;

        var firstSeenAs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RunLootCaptureDto capture in captures)
        {
            int number = Captures.Count + 1;
            int? repeatOf = null;
            if (capture.ContentHash is { } hash)
            {
                if (firstSeenAs.TryGetValue(hash, out int earlier))
                    repeatOf = earlier;
                else
                    firstSeenAs[hash] = number;
            }

            Captures.Add(new RunLootCaptureRowViewModel(capture, repeatOf, number));
        }

        _MarkAddedAfterEdit();
        _Recompute();
    }

    /// <summary>Excludes or re-includes a capture and updates the total to match. Never removes the row — exclusion
    /// is a flag, not a deletion — and it is the model's, not a button's: writing the list out by hand excludes the
    /// captures it was written from through this same flag.</summary>
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

    /// <summary>The one way back in the strip offers: a capture kept out because it repeated an earlier one, for the
    /// rare run where the same thing really was looted twice.</summary>
    [RelayCommand]
    private Task ReincludeCaptureAsync(RunLootCaptureRowViewModel row) => ToggleExcludedAsync(row);

    [RelayCommand]
    private void BeginLootEdit()
    {
        LootText = _AsPasteText();
        LootTextRefusal = null;
        IsEditingLoot = true;
    }

    [RelayCommand]
    private void CancelLootEdit()
    {
        IsEditingLoot = false;
        LootText = null;
        LootTextRefusal = null;
    }

    /// <summary>Reading it happens as it is typed so "done" can be greyed with the reason beside it, rather than
    /// accepting the box and quietly dropping the row that could not be read.</summary>
    partial void OnLootTextChanged(string? value) =>
        LootTextRefusal = _sde is null || string.IsNullOrWhiteSpace(value)
            ? null
            : InventoryTextReading.Read(value, _sde).Refusal;

    /// <summary>
    /// The written-out list becomes the loot: one capture of its own, with every capture it was written from
    /// excluded. The truth underneath stays entries and never text — the text is derivable from the rows, and a
    /// stored copy of it would be a second answer that can drift from the one the saved run is rebuilt out of.
    /// </summary>
    public async Task<bool> ReplaceLootWithTextAsync(CancellationToken cancellationToken = default)
    {
        if (RunId is not { } runId || _sde is null || string.IsNullOrWhiteSpace(LootText))
            return false;

        InventoryTextReading reading = InventoryTextReading.Read(LootText, _sde);
        if (reading.Lines.Count == 0)
        {
            LootTextRefusal = reading.Refusal ?? "Nothing in this text reads as an EVE inventory listing.";
            return false;
        }

        Result<Guid> stored = await _dispatcher.Send(new SetRunLootManualCommand(runId, DateTime.UtcNow,
            [.. reading.Lines.Select(resolved => new RunLootEntryInput
            {
                ItemTypeId = resolved.Line.TypeId,
                Name = resolved.Line.Name,
                Quantity = resolved.Line.Quantity,
                // The clipboard columns as they stood in the window, not a valuation: the money comes from the
                // type-id lookup here as it does everywhere else.
                Volume = resolved.Item.Volume,
                ClipboardPrice = resolved.Item.Price,
                LootKind = LootKind.Gained
            })]), cancellationToken);
        if (!stored.IsSuccess)
        {
            LootTextRefusal = stored.Messages.Count > 0 ? stored.Messages[0].Text : "This list was not stored.";
            return false;
        }

        IsEditingLoot = false;
        LootText = null;
        LootTextRefusal = null;
        await RefreshAsync(cancellationToken);
        return true;
    }

    /// <summary>The "done" button. Greyed on <see cref="CanFinishLootEdit"/> rather than refusing after the click.
    /// </summary>
    [RelayCommand]
    private Task FinishLootEditAsync() => ReplaceLootWithTextAsync();

    /// <summary>The list in the form EVE itself copies — name, tab, quantity — so what comes out of the box can go
    /// straight back into it, and a row can be pasted in from the game beside the ones already there.</summary>
    private string _AsPasteText() => string.Join(Environment.NewLine, _counted.Select(line =>
        $"{_names.GetValueOrDefault(line.ItemTypeId, line.ItemTypeId.ToString(CultureInfo.InvariantCulture))}\t"
        + (line.Quantity ?? 1).ToString(CultureInfo.InvariantCulture)));

    partial void OnCargoBeforeTextChanged(string? value) =>
        _TrackCargoWrite(PasteCargoAsync(LootCaptureRole.CargoBefore, value));

    partial void OnCargoAfterTextChanged(string? value) =>
        _TrackCargoWrite(PasteCargoAsync(LootCaptureRole.CargoAfter, value));

    /// <summary>
    /// Reads a cargo hold out of one of the boxes and stores it as that run's hold, replacing whatever the box held
    /// before — pasting again is a correction of the same hold, not a second sighting of it.
    ///
    /// Text that cannot be read blocks nothing and is not thrown away: it stays in the box with the reason under it,
    /// because a hold nobody could read belongs to a role you have not handed out yet. The stored hold from the
    /// previous, readable paste is left exactly where it was rather than being emptied on a typo.
    /// </summary>
    public async Task<bool> PasteCargoAsync(LootCaptureRole role, string? text, CancellationToken cancellationToken = default)
    {
        if (RunId is not { } runId || _sde is null)
            return false;

        if (string.IsNullOrWhiteSpace(text))
        {
            _SetCargoStatus(role, null);
            return false;
        }

        InventoryTextReading reading = InventoryTextReading.Read(text, _sde);
        if (reading.Lines.Count == 0)
        {
            _SetCargoStatus(role, reading.Refusal ?? "Nothing in this text reads as an EVE inventory listing.");
            return false;
        }

        Result<Guid> stored = await _dispatcher.Send(new SetRunCargoHoldCommand(runId, role, DateTime.UtcNow,
            [.. reading.Lines.Select(resolved => new RunLootEntryInput
            {
                ItemTypeId = resolved.Line.TypeId,
                Name = resolved.Line.Name,
                Quantity = resolved.Line.Quantity,
                // The clipboard columns as they stood in the window, not a valuation: the money comes from the
                // type-id lookup here as it does everywhere else.
                Volume = resolved.Item.Volume,
                ClipboardPrice = resolved.Item.Price,
                LootKind = LootKind.Gained
            })]), cancellationToken);
        if (!stored.IsSuccess)
        {
            _SetCargoStatus(role, stored.Messages.Count > 0 ? stored.Messages[0].Text : "This cargo hold was not stored.");
            return false;
        }

        int unresolved = reading.UnresolvedCount;
        _SetCargoStatus(role, unresolved > 0
            ? $"read: {reading.Lines.Count} row(s), {unresolved} name(s) not recognised"
            : $"read: {reading.Lines.Count} row(s)");
        await RefreshAsync(cancellationToken);
        return true;
    }

    /// <summary>Which capture the run started from, changed after the fact. The capture that held the role keeps its
    /// place in the strip and says what it was — a correction is not allowed to make a cargo hold disappear.</summary>
    public async Task<bool> MakeCargoBeforeAsync(RunLootCaptureRowViewModel row, CancellationToken cancellationToken = default)
    {
        Result result = await _dispatcher.Send(new SetRunLootCaptureRoleCommand(row.CaptureId, LootCaptureRole.CargoBefore), cancellationToken);
        if (!result.IsSuccess)
        {
            RunStatusMessage = result.Messages.Count > 0 ? result.Messages[0].Text : null;
            return false;
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    private void _SetCargoStatus(LootCaptureRole role, string? status)
    {
        if (role is LootCaptureRole.CargoBefore)
            CargoBeforeStatus = status;
        else
            CargoAfterStatus = status;
    }

    private void _TrackCargoWrite(Task current) => LastCargoWrite = Task.WhenAll(LastCargoWrite, current);

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
    /// Prices are read once per refresh and held; re-totalling works from what is already here rather than asking
    /// again. A cache with nothing in it comes back as a failure, which becomes the label under the figures instead
    /// of a silent zero.
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

    /// <summary>Everything that arrived after the hand-written list. Derived from the order rather than stored: the
    /// mark means "later than the capture the pilot wrote", and the list already says which one that is.</summary>
    private void _MarkAddedAfterEdit()
    {
        int manual = Captures.Count - 1;
        while (manual >= 0 && Captures[manual].Source is not LootCaptureSource.Manual)
            manual--;

        for (int index = 0; index < Captures.Count; index++)
            Captures[index].IsAddedAfterEdit = manual >= 0 && index > manual && !Captures[index].IsExcluded;
    }

    /// <summary>Which captures count, and for how much, is <see cref="LootTally"/>'s answer and not this window's —
    /// the stored run is rebuilt from the same rule, so the figures here are the figures it will keep, and the rows
    /// on screen are the rows those figures are made of.</summary>
    private void _Recompute()
    {
        _counted = LootTally.Count(_TallyCaptures());
        TotalIsk = _Sum(_counted);
        EntriesWithoutPrice = _counted.Count(line => !_unitPrices.ContainsKey(line.ItemTypeId));
        LootIsk = _Sum(_counted.Where(line => line.LootKind == LootKind.Gained));
        ConsumedIsk = _Sum(_counted.Where(line => line.LootKind == LootKind.Lost));
        NetIsk = LootIsk is null && ConsumedIsk is null ? null : (LootIsk ?? 0m) - (ConsumedIsk ?? 0m);

        CountedLines.Clear();
        foreach (LootTallyLine line in _counted)
            CountedLines.Add(new ActivityLootLineViewModel(
                _names.GetValueOrDefault(line.ItemTypeId, $"type {line.ItemTypeId}"),
                line.Quantity, _UnitPrice(line.ItemTypeId), line.LootKind));

        foreach (RunLootCaptureRowViewModel capture in Captures)
            capture.SubtotalDisplay = _Display(_Sum(
                capture.Entries.Select(entry => new LootTallyLine(entry.ItemTypeId, entry.Quantity, Volume: null, entry.LootKind))));

        OnPropertyChanged(nameof(TotalIskLabel));
        OnPropertyChanged(nameof(DifferenceText));
        OnPropertyChanged(nameof(CanEditLoot));
        OnPropertyChanged(nameof(CargoBeforeCapture));
        OnPropertyChanged(nameof(ManualListCaption));
        OnPropertyChanged(nameof(AddedAfterEditNote));
    }

    /// <summary>No volume: a capture row carries none, and nothing on this screen totals one.</summary>
    private IReadOnlyList<LootTallyCapture> _TallyCaptures() =>
        [.. Captures.Select(capture => new LootTallyCapture(capture.Role, capture.IsExcluded,
            [.. capture.Entries.Select(entry => new LootTallyLine(entry.ItemTypeId, entry.Quantity, Volume: null, entry.LootKind))]))];

    private decimal? _UnitPrice(int itemTypeId) => _unitPrices.TryGetValue(itemTypeId, out decimal price) ? price : null;

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
