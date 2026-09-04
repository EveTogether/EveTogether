using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One of my characters shown as a member-leaf under a fleet node in the Fleets window: the
/// pilot's role, the fit they fly, a can-fly badge, and a SELECT FIT action that opens the
/// composition-scoped picker so the pilot picks their OWN fit (master-plan §5; the server authorizes owner-or-self).
/// The command is supplied by <see cref="FleetsViewModel"/>, which owns the (server, character, composition) context.
/// </summary>
public sealed partial class FleetMemberRowViewModel : ObservableObject, IFleetMemberMenuHost
{
    public FleetMemberRowViewModel(
        long memberId, int characterId, string characterName, string roleLabel,
        FitReferenceInfo? assignedFit, MemberSkillBadge? skillBadge, IAsyncRelayCommand selectFitCommand,
        IAsyncRelayCommand? openFitCommand = null, IAsyncRelayCommand? leaveCommand = null, bool canLeave = false,
        FleetMemberFacts? menuFacts = null, IRelayCommand? removeCommand = null,
        bool isMine = false, bool isFleetCommander = false, DateTimeOffset? lastSeenAt = null)
    {
        IsMine = isMine;
        IsFleetCommander = isFleetCommander;
        LastSeenAt = lastSeenAt;
        ShipName = menuFacts?.ShipName;
        MemberId = memberId;
        CharacterId = characterId;
        CharacterName = characterName;
        RoleLabel = roleLabel;
        AssignedFit = assignedFit;
        SelectFitCommand = selectFitCommand;
        OpenFitCommand = openFitCommand;
        LeaveCommand = leaveCommand;
        CanLeave = canLeave;
        if (skillBadge is not null)
        {
            HasSkillBadge = true;
            CanFly = skillBadge.CanFly;
            SkillTooltip = skillBadge.Tooltip;
        }

        // The shared fleet-member menu (ET-44). This card carries a client-only fleet's WHOLE roster since ET-46 —
        // externals included, and an external pilot has no card of their own anywhere else in this window — so the
        // pilot summary and the owner's removal belong here too. No facts supplied = no menu (a caller that has not
        // been taught them shows nothing rather than a menu of blanks).
        MemberMenu = menuFacts is null ? [] : FleetMemberMenu.Build(menuFacts, DateTimeOffset.UtcNow, removeCommand);
        IsExternal = menuFacts?.IsExternal ?? false;
    }

    /// <summary>This pilot is an external — not coupled on this client, and so with no row anywhere but on this card
    /// (ET-46). The card counts them separately when it shortens its list, because a hidden external is the one thing
    /// a fold can put out of reach (ET-53).</summary>
    public bool IsExternal { get; }

    /// <summary>The shared fleet-member information block, plus the removal when this viewer owns the fleet.</summary>
    public IReadOnlyList<FleetMemberMenuItemViewModel> MemberMenu { get; }

    // ── The overview's sub-row (ET-170): who this is to me, whether they are here, and whether they count. ──

    /// <summary>One of this client's own characters — the reason the fleet row concerns me at all.</summary>
    public bool IsMine { get; }

    /// <summary>Holds the fleet-commander seat on the ET roster.</summary>
    public bool IsFleetCommander { get; }

    /// <summary>The ship the assigned fit flies, or null when no fit is assigned.</summary>
    public string? ShipName { get; }

    public string ShipText => ShipName ?? "—";

    /// <summary>When the server last heard this member's client, as read from the roster; feeds the presence verdict.</summary>
    public DateTimeOffset? LastSeenAt { get; }

    /// <summary>Presence as the one shared definition reads it (ET-70): our own pilot from the local sweep, anyone
    /// else from how long their client has been silent. Set by the overview rebuild, not at construction, because it
    /// needs the clock.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(IsPresenceUnknown))]
    private FleetMemberPresenceState _presence = FleetMemberPresenceState.Unknown;

    public bool IsOnline => Presence == FleetMemberPresenceState.Online;
    public bool IsOffline => Presence == FleetMemberPresenceState.Offline;
    public bool IsPresenceUnknown => Presence == FleetMemberPresenceState.Unknown;

    public string PresenceText => Presence switch
    {
        FleetMemberPresenceState.Online => "online",
        FleetMemberPresenceState.Offline => "offline",
        _ => "unknown",
    };

    /// <summary>Whether the member counts for this fleet — the axis this whole screen exists for. Set by the
    /// overview rebuild once every started fleet is known, since the answer depends on the others.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkText))]
    [NotifyPropertyChangedFor(nameof(IsElsewhereActive))]
    [NotifyPropertyChangedFor(nameof(IsLinkDim))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    private FleetMemberLinkState _linkState = FleetMemberLinkState.StandingBy;

    public bool IsElsewhereActive => LinkState == FleetMemberLinkState.ElsewhereActive;

    /// <summary>"standing by", "no client" and "finished" are states of the fleet, not of the pilot, and read dim.</summary>
    public bool IsLinkDim => LinkState is FleetMemberLinkState.StandingBy or FleetMemberLinkState.NoClient or FleetMemberLinkState.Finished;

    public string LinkText => LinkState switch
    {
        FleetMemberLinkState.Linked => "linked",
        FleetMemberLinkState.ElsewhereActive => "not linked",
        FleetMemberLinkState.StandingBy => "standing by",
        FleetMemberLinkState.NoClient => "no client",
        _ => "—",
    };

    /// <summary>One of my characters whose sharing for this fleet is switched off: on the roster, linked, and yet
    /// contributing nothing — the third axis, and the third reason a member belongs on the short list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private bool _sharesNothing;

    /// <summary>The member the fold may never hide (ET-170): not linked while the fleet runs, or sharing nothing.
    /// Being offline is counted, not flagged — it is presence, not participation.</summary>
    public bool NeedsAttention => IsElsewhereActive || SharesNothing;

    /// <summary>
    /// Why this member sits on the roster and yet does not count here: the started fleet they are linked to instead.
    /// Null unless they are elsewhere active. Scherm 1 spells this out under the row rather than leaving a bare
    /// "not linked" to be guessed at — the whole screen exists to make this one situation readable, and "not linked"
    /// on its own is exactly what a reader mistakes for "offline".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasElsewhereNote))]
    private string? _elsewhereNote;

    public bool HasElsewhereNote => !string.IsNullOrEmpty(ElsewhereNote);

    // ── The one act on an elsewhere-active member (ET-168, scherm 1) ────────────────────────────────────────────

    /// <summary>
    /// What to do about a member who counts somewhere else: move them, or ask them to move. Supplied by the
    /// overview's rebuild, because whether it applies depends on <see cref="LinkState"/>, which is only known once
    /// every started fleet has been read. Null means there is nothing to offer here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    private IAsyncRelayCommand? _switchCommand;

    /// <summary>Only ever shown on a member who is elsewhere active — the row's whole reason for having it.</summary>
    public bool CanSwitch => IsElsewhereActive && SwitchCommand is not null;

    /// <summary>
    /// Two verbs, and the difference between them is the whole rule: your own pilot you move, because it is your
    /// character; anyone else's you ask, because which fleet a player stays in is theirs to decide. A commander
    /// never pulls someone out of another fleet.
    /// </summary>
    public string SwitchLabel => IsMine ? "switch" : "ask to switch";

    public string SwitchTooltip => IsMine
        ? "Move this pilot here: leave the fleet it counts for, then link here"
        : "Send this pilot a request to come over. They decide; nothing moves until they do.";

    /// <summary>The fleet commander, this client's own characters, and whoever asks for attention: the members
    /// a folded row always shows, whether the fleet has six pilots or fifty.</summary>
    public bool IsHighlighted => IsFleetCommander || IsMine || NeedsAttention;

    public long MemberId { get; }
    public int CharacterId { get; }
    public string CharacterName { get; }

    /// <summary>The pilot's position in the fleet structure (FC / Wing / Squad / Unassigned), for the leaf row.</summary>
    public string RoleLabel { get; }

    /// <summary>The fit this pilot flies, or null when none is assigned yet.</summary>
    public FitReferenceInfo? AssignedFit { get; }

    public bool HasAssignedFit => AssignedFit is not null;
    public string AssignedFitName => AssignedFit?.FitName ?? "— no fit selected —";

    /// <summary>SELECT FIT when none is assigned, CHANGE FIT to replace the current one.</summary>
    public string SelectFitButtonLabel => HasAssignedFit ? "CHANGE FIT" : "SELECT FIT";

    /// <summary>can-fly verdict: no badge when there is no fit, the character's skills are not locally
    /// known, or the SDE is unavailable (unknown ≠ "can't fly").</summary>
    public bool HasSkillBadge { get; }
    public bool CanFly { get; }
    public string SkillTooltip { get; } = "";

    /// <summary>The "can fly" badge shows only when there is a verdict AND the pilot trains every required skill.</summary>
    public bool ShowCanFly => HasSkillBadge && CanFly;

    /// <summary>The "skills missing" badge shows only when there is a verdict AND at least one skill is short.</summary>
    public bool ShowSkillGap => HasSkillBadge && !CanFly;

    /// <summary>A neutral "?" shows when a fit is assigned but there is no verdict at all — neither computed locally
    /// nor reported by the pilot's client — so the gap is visible (and explained) instead of silently blank.</summary>
    public bool ShowSkillUnknown => HasAssignedFit && !HasSkillBadge;

    public string UnknownSkillTooltip =>
        "Can-fly unknown: this character's skills aren't known locally and the pilot's client hasn't reported a verdict. " +
        "Sign this character in with the read_skills scope (and import skills) to see a can-fly check.";

    /// <summary>Opens the composition-scoped single fit picker and persists the pick (owner-or-self, master-plan §5).</summary>
    public IAsyncRelayCommand SelectFitCommand { get; }

    /// <summary>Opens the read-only fit detail for the assigned fit so any member's fit can be inspected from the
    /// fleet list; null/disabled when no fit is assigned.</summary>
    public IAsyncRelayCommand? OpenFitCommand { get; }

    /// <summary>Pulls this one character out of the fleet: set only for my non-owner characters on a
    /// server fleet, so an alt I fly in a fleet I own can leave while the owner stays. Null for the owner's own
    /// character (the owner disbands/transfers instead) and for local fleets.</summary>
    public IAsyncRelayCommand? LeaveCommand { get; }

    /// <summary>Drives the leaf's LEAVE button — true for a non-owner character on a server fleet.</summary>
    public bool CanLeave { get; }

    /// <summary>The pilot's ESI portrait for the hex on the leaf row (stream B / B-3, mirrors the character column's
    /// hex portrait); null until loaded or when images are off/offline, so the leaf falls back to the initial glyph.</summary>
    [ObservableProperty] private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;

    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>First letter of the name, shown in the hex when no portrait render is available (offline/disabled/external).</summary>
    public string Initial => string.IsNullOrEmpty(CharacterName) ? "?" : CharacterName[..1].ToUpperInvariant();

    /// <summary>Loads the ESI portrait best-effort (opt-in image setting); a failure leaves the glyph fallback.</summary>
    public async Task LoadPortraitAsync(ICharacterPortraitProvider portraits, CancellationToken cancellationToken = default)
    {
        if (CharacterId <= 0)
            return;
        Portrait = await portraits.GetPortraitAsync(CharacterId, 64, cancellationToken);
    }
}
