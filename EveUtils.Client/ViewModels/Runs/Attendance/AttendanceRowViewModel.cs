using System;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// One character on a homefront's attendance list (ET-230), in the run window and on the detail screen alike: the
/// tick for "in site at completion", why it stands where it stands, and the live presence beside it. Every state is
/// said in words — ticked or not, why, online or not — so none of it rests on a colour alone.
/// </summary>
public sealed partial class AttendanceRowViewModel : ObservableObject
{
    private readonly Action<AttendanceRowViewModel>? _onTicked;
    private bool _isApplying;

    public AttendanceRowViewModel(long characterId, string name, bool isLocal, bool isExternal,
        Action<AttendanceRowViewModel>? onTicked = null)
    {
        CharacterId = characterId;
        _name = name;
        IsLocal = isLocal;
        IsExternal = isExternal;
        _onTicked = onTicked;
    }

    public long CharacterId { get; }

    [ObservableProperty] private string _name;

    /// <summary>One of this client's own characters — "Local", the one thing a client knows for certain; never
    /// "yours", which it does not.</summary>
    public bool IsLocal { get; }

    public bool IsExternal { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TickText))]
    private bool _isInSite;

    /// <summary>Only for the one who decides; everyone else reads the list without being able to change it.</summary>
    [ObservableProperty] private bool _isEditable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(IsWithoutEvidence))]
    private AttendanceReason _reason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private long? _reasonAmount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private bool _isAddedToLastSite;

    /// <summary>Who set this row against the proposal — "you", or the commander's name — or null when it stands as
    /// proposed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetByHand))]
    [NotifyPropertyChangedFor(nameof(SetByText))]
    [NotifyPropertyChangedFor(nameof(IsWithoutEvidence))]
    private string? _setBy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsPresenceShown))]
    private FleetMemberPresenceState? _presence;

    public string TickText => IsInSite ? "in site" : "not in site";

    public string ReasonText => Describe(Reason, ReasonAmount, IsLocal, IsExternal) + (IsAddedToLastSite ? " — added" : string.Empty);

    /// <summary>Nothing to go on and nobody has looked at it yet — the row the one who decides has to check, marked but
    /// never greyed out: the hauler has to stay readable. Once set by hand it has been looked at.</summary>
    public bool IsWithoutEvidence => Reason is AttendanceReason.NoActivityLogged && SetBy is null;

    public bool IsSetByHand => SetBy is not null;

    public string SetByText => SetBy is { } who ? $"set by {who}" : string.Empty;

    /// <summary>Live, never stored: an external pilot says what they are, anyone else what the fleet sees right now,
    /// and nothing at all when there is no fleet to ask.</summary>
    public string? PresenceText => IsExternal
        ? "external · no Eve Together"
        : Presence switch
        {
            FleetMemberPresenceState.Online => "● online",
            FleetMemberPresenceState.Offline => "○ offline",
            FleetMemberPresenceState.Unknown => "? unknown",
            _ => null
        };

    public bool IsOnline => !IsExternal && Presence is FleetMemberPresenceState.Online;

    public bool IsPresenceShown => PresenceText is not null;

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

    partial void OnIsInSiteChanged(bool value)
    {
        if (!_isApplying && IsEditable)
            _onTicked?.Invoke(this);
    }

    /// <summary>Why a character stands where it stands, in words. Shared with the detail screen so both say it the
    /// same way.</summary>
    public static string Describe(AttendanceReason reason, long? amount, bool isLocal, bool isExternal) => reason switch
    {
        AttendanceReason.DamageDealt => $"{_Figure(amount)} damage dealt",
        AttendanceReason.RemoteRepair => $"remote repair {_Figure(amount)}",
        AttendanceReason.RemoteCapacitor => $"remote capacitor {_Figure(amount)}",
        AttendanceReason.Salvaged => amount == 1 ? "salvaged 1 wreck" : $"salvaged {_Figure(amount)} wrecks",
        AttendanceReason.Mined => $"mined {IskFormat.Number(amount ?? 0)} units",
        AttendanceReason.FleetActivity => "had activity this run",
        AttendanceReason.SameAsLastSite => "same as last site",
        // Who set it is said beside this, by the row's own "set by".
        AttendanceReason.SetByHand => string.Empty,
        _ when isExternal => "no evidence can arrive — tick if in site",
        _ when isLocal => "no activity logged",
        _ => "no activity reported by its client"
    };

    private static string _Figure(long? amount) => IskFormat.Compact(amount ?? 0);
}
