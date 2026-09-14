using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// One character on a homefront's attendance list (ET-230), in the run window and on the detail screen alike: one line
/// with the name, a hint of what the evidence says, what this character is paid, and the tick for "in site at
/// completion". Every state is said in words — ticked or not, why, a typed amount — so none of it rests on a colour
/// alone.
///
/// A different amount is an exception (ET-271): the figure is a link, and only clicking it opens the field, already
/// holding the figure it replaces, so correcting 15,000,000 to 14,000,000 is a few keys and Enter.
/// </summary>
public sealed partial class AttendanceRowViewModel : ObservableObject
{
    private readonly Action<AttendanceRowViewModel>? _onTicked;
    private readonly Action<AttendanceRowViewModel, decimal?>? _onEnterPayout;
    private bool _isApplying;

    public AttendanceRowViewModel(long characterId, string name, bool isLocal, bool isExternal,
        Action<AttendanceRowViewModel>? onTicked = null,
        Action<AttendanceRowViewModel, decimal?>? onEnterPayout = null)
    {
        CharacterId = characterId;
        _name = name;
        IsLocal = isLocal;
        IsExternal = isExternal;
        _onTicked = onTicked;
        _onEnterPayout = onEnterPayout;
    }

    public long CharacterId { get; }

    [ObservableProperty] private string _name;

    /// <summary>One of this client's own characters — "Local", the one thing a client knows for certain; never
    /// "yours", which it does not.</summary>
    public bool IsLocal { get; }

    public bool IsExternal { get; }

    /// <summary>Zebra striping on the activity detail screen (ET-285), set by the caller once the row's final
    /// position in the (Local-first, then alphabetical) list is known.</summary>
    [ObservableProperty] private bool _isAlternate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TickText))]
    [NotifyPropertyChangedFor(nameof(PayoutText))]
    [NotifyPropertyChangedFor(nameof(IsPayoutShown))]
    [NotifyPropertyChangedFor(nameof(IsTypedShown))]
    [NotifyPropertyChangedFor(nameof(CanActOnPayout))]
    [NotifyPropertyChangedFor(nameof(IsPayoutReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(BeginPayoutEditCommand))]
    private bool _isInSite;

    /// <summary>Only for the one who decides; everyone else reads the list without being able to change it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnPayout))]
    [NotifyPropertyChangedFor(nameof(IsPayoutReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(BeginPayoutEditCommand))]
    private bool _isEditable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private AttendanceReason _reason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private long? _reasonAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private bool _isAddedToLastSite;

    /// <summary>Who set this row against the proposal, as a reader who cannot change it sees it — the commander's
    /// name — or null. The one who decides needs no "set by you" beside their own click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetByHand))]
    [NotifyPropertyChangedFor(nameof(SetByText))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private string? _setBy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    [NotifyPropertyChangedFor(nameof(IsPresenceShown))]
    [NotifyPropertyChangedFor(nameof(HintText))]
    private FleetMemberPresenceState? _presence;

    // ── The payout (ET-231, ET-271) ─────────────────────────────────────────────────────────────────

    /// <summary>What the table pays at the current N, with no regard for outcome — what a correction starts from.
    /// Null while N is not known, the site is not a homefront, or N is beyond the table.</summary>
    [ObservableProperty] private decimal? _tablePayoutIsk;

    /// <summary>The table's own figure at the current N, owed the moment the site reads Completed (ET-269: there is
    /// no wallet to wait on) — or, for AAR, once it has paid waves. Null until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayoutText))]
    [NotifyPropertyChangedFor(nameof(IsPayoutShown))]
    [NotifyPropertyChangedFor(nameof(IsTypedShown))]
    [NotifyPropertyChangedFor(nameof(CanActOnPayout))]
    [NotifyPropertyChangedFor(nameof(IsPayoutReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(BeginPayoutEditCommand))]
    private decimal? _expectedPayoutIsk;

    /// <summary>What the pilot typed instead, because something else really arrived — counted in place of
    /// <see cref="ExpectedPayoutIsk"/> while the payout is owed, never a step the figure needs. Null until typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayoutText))]
    [NotifyPropertyChangedFor(nameof(IsCorrected))]
    [NotifyPropertyChangedFor(nameof(IsTypedShown))]
    private decimal? _correctedPayoutIsk;

    /// <summary>Whether this character has a run of its own here to write a typed figure onto — an own character
    /// only on the roster has none until the list puts it in the site (ET-269).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnPayout))]
    [NotifyPropertyChangedFor(nameof(IsPayoutReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(BeginPayoutEditCommand))]
    private bool _hasRun = true;

    /// <summary>This client's own, ticked, owed row can take a correction — a group-mate's payout is corrected on
    /// their own client, never this one.</summary>
    public bool CanActOnPayout => IsEditable && HasRun && IsLocal && IsInSite && ExpectedPayoutIsk is not null;

    public bool IsPayoutShown => IsLocal && IsInSite && ExpectedPayoutIsk is not null;

    public bool IsPayoutReadOnly => IsPayoutShown && !CanActOnPayout;

    public bool IsCorrected => CorrectedPayoutIsk is not null;

    /// <summary>"typed" beside a figure that is on screen — never beside nothing, on a site that no longer pays.</summary>
    public bool IsTypedShown => IsCorrected && IsPayoutShown;

    public string PayoutText => CorrectedPayoutIsk is { } corrected
        ? IskFormat.Number(corrected)
        : ExpectedPayoutIsk is { } expected ? IskFormat.Number(expected) : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPayoutLinkShown))]
    private bool _isEditingPayout;

    public bool IsPayoutLinkShown => !IsEditingPayout;

    [ObservableProperty] private string _payoutEditText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanActOnPayout))]
    private void BeginPayoutEdit()
    {
        PayoutEditText = IskFormat.Number(CorrectedPayoutIsk ?? ExpectedPayoutIsk ?? 0m);
        IsEditingPayout = true;
    }

    /// <summary>Enter: the typed figure replaces the table's — or, typed back to the table's own, drops the
    /// correction instead of marking an unchanged figure as corrected.</summary>
    [RelayCommand]
    private void CommitPayoutEdit()
    {
        if (!IskFormat.TryParseWhole(PayoutEditText, out decimal amount))
            return;

        IsEditingPayout = false;
        decimal? correction = amount == TablePayoutIsk ? null : amount;
        if (correction == CorrectedPayoutIsk)
            return;

        CorrectedPayoutIsk = correction;
        _onEnterPayout?.Invoke(this, correction);
    }

    [RelayCommand]
    private void CancelPayoutEdit() => IsEditingPayout = false;

    public string TickText => IsInSite ? "in site" : "not in site";

    public string ReasonText => Describe(Reason, ReasonAmount) + (IsAddedToLastSite ? " — added" : string.Empty);

    public bool IsSetByHand => SetBy is not null;

    public string SetByText => SetBy is { } who ? $"set by {who}" : string.Empty;

    /// <summary>Only what explains a tick: an external pilot says what they are, and a character this client knows to
    /// be logged out says so — the reason it starts out of the site. Online is the normal case and says nothing.</summary>
    public string? PresenceText => IsExternal
        ? "external"
        : Presence is FleetMemberPresenceState.Offline ? "offline" : null;

    public bool IsPresenceShown => PresenceText is not null;

    /// <summary>The hint beside the name, in one run of words — "offline", "1.2M damage", "set by Jithran" — or
    /// nothing at all for a character simply in the site.</summary>
    public string HintText => string.Join(" · ", new[] { PresenceText, ReasonText, SetByText }
        .Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>Set the row from a list without it counting as a tick somebody made.</summary>
    public void Show(bool isInSite, AttendanceReason reason, long? amount, bool isAddedToLastSite, string? setBy)
    {
        _isApplying = true;
        try
        {
            IsInSite = isInSite;
            Reason = reason;
            ReasonAmount = amount;
            IsAddedToLastSite = isAddedToLastSite;
            SetBy = setBy;
        }
        finally
        {
            _isApplying = false;
        }
    }

    /// <summary>Set the row's payout figures (ET-231) — independent of <see cref="Show"/>, since the payout is
    /// recomputed on its own clock (N, outcome, a correction) rather than alongside every tick.</summary>
    public void ShowPayout(decimal? tablePayoutIsk, decimal? expectedPayoutIsk, decimal? correctedPayoutIsk)
    {
        TablePayoutIsk = tablePayoutIsk;
        ExpectedPayoutIsk = expectedPayoutIsk;
        CorrectedPayoutIsk = correctedPayoutIsk;
    }

    partial void OnIsInSiteChanged(bool value)
    {
        if (!_isApplying && IsEditable)
            _onTicked?.Invoke(this);
    }

    /// <summary>Why a character stands where it stands, in words. Shared with the detail screen so both say it the
    /// same way. Nothing at all for a line nobody has evidence about: in the site is the normal case (ET-271), and a
    /// hint that says nothing is noise.</summary>
    public static string Describe(AttendanceReason reason, long? amount) => reason switch
    {
        AttendanceReason.DamageDealt => $"{_Figure(amount)} damage",
        AttendanceReason.RemoteRepair => $"{_Figure(amount)} remote repair",
        AttendanceReason.RemoteCapacitor => $"{_Figure(amount)} remote cap",
        AttendanceReason.Salvaged => amount == 1 ? "salvaged 1 wreck" : $"salvaged {_Figure(amount)} wrecks",
        AttendanceReason.Mined => $"mined {IskFormat.Number(amount ?? 0)} units",
        AttendanceReason.FleetActivity => "active this run",
        AttendanceReason.SameAsLastSite => "as last site",
        _ => string.Empty
    };

    private static string _Figure(long? amount) => IskFormat.Compact(amount ?? 0);
}
