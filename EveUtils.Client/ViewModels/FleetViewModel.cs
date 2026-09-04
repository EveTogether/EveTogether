using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Fleets;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One fleet row in the Fleets window. Wraps a <see cref="FleetInfo"/> and resolves the display labels +
/// whether the acting character owns it (drives the edit/disband buttons). The action commands live on the parent
/// <see cref="FleetsViewModel"/> and take this row as their parameter.
/// </summary>
public sealed partial class FleetViewModel : ObservableObject
{
    public FleetViewModel(FleetInfo fleet, int actingCharacterId, string characterName = "",
        string? serverAddress = null, string? serverName = null)
    {
        Info = fleet;
        Id = fleet.Id;
        Name = fleet.Name;
        Description = fleet.Description;
        ActingCharacterId = actingCharacterId;
        CharacterName = characterName;
        ServerAddress = serverAddress;
        ServerName = serverName;
        VisibilityLabel = fleet.Visibility == FleetVisibility.Public ? "Public" : "Invite-only";
        IsMine = fleet.CreatorCharacterId == actingCharacterId;
        IsPublic = fleet.Visibility == FleetVisibility.Public;
        IsInviteOnly = fleet.Visibility == FleetVisibility.InviteOnly;
        ActivationLabel = fleet.Activation switch
        {
            FleetActivation.Active => "Active",
            FleetActivation.Concluded => "Concluded",
            _ => "Forming"
        };
        StatusLabel = $"{VisibilityLabel} · {ActivationLabel}";
        StateLabel = ActivationLabel.ToUpperInvariant();
        IsForming = fleet.Activation == FleetActivation.Forming;
        Group = fleet.Activation.ToGroup();
        // Which of my coupled characters this row belongs to: owner for my fleets, the
        // member character for participating ones. Shown on the row and used as the acting char for its actions.
        CharacterLabel = string.IsNullOrEmpty(characterName) ? "" : $"{characterName}{(IsMine ? " · owner" : "")}";
    }

    /// <summary>The full source info, kept for the edit-dialog prefill.</summary>
    public FleetInfo Info { get; }

    /// <summary>The server this fleet lives on — the target for every per-row action.
    /// Null for a client-only fleet, which has no server.</summary>
    public string? ServerAddress { get; }

    /// <summary>The coupled server's display name, used for the per-server grouping header in the listing.</summary>
    public string? ServerName { get; }

    /// <summary>Where this fleet lives, as the one key the link rule tells fleets apart by (ET-170).</summary>
    public FleetKey Key => new(ServerAddress, Id);

    public long Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public string VisibilityLabel { get; }

    /// <summary>The coupled character this row is listed for — the acting char for ENTER/MANAGE/LEAVE on this row.</summary>
    public int ActingCharacterId { get; }
    public string CharacterName { get; }

    /// <summary>"Lionear · owner" / "Maricadie" — which of my characters this fleet row belongs to (aggregate listing).</summary>
    public string CharacterLabel { get; }

    /// <summary>The acting character is the creator → may edit/disband.</summary>
    public bool IsMine { get; }

    /// <summary>Public fleets show the JOIN button; invite-only show REQUEST.</summary>
    public bool IsPublic { get; }
    public bool IsInviteOnly { get; }

    /// <summary>Forming / Active / Concluded — shown in the browser status line.</summary>
    public string ActivationLabel { get; }

    /// <summary>The fleet's activation state as an uppercase pill label (B-3, option B): the state lives on its own
    /// "ACTIVE / FORMING" pill, separate from the green participation dot (<see cref="IsActive"/>) which means
    /// "the fleet I'm currently in" — two distinct signals, not one overloaded dot.</summary>
    public string StateLabel { get; }

    /// <summary>The fleet is still forming → the state pill is amber rather than the accent green of an active fleet.</summary>
    public bool IsForming { get; }

    /// <summary>Combined visibility + activation status for the DISCOVER list (member count appended live).</summary>
    [ObservableProperty] private string _statusLabel = "";

    /// <summary>This is the fleet the client is currently participating in — drives the active highlight.</summary>
    [ObservableProperty] private bool _isActive;

    // ── The overview's three bands (ET-170) ─────────────────────────────────────────────────────────────────────

    /// <summary>Which band of the overview this row sits in, read off the fleet's activation.</summary>
    public FleetOverviewGroup Group { get; }

    public bool IsInActiveGroup => Group == FleetOverviewGroup.Active;
    public bool IsStandingBy => Group == FleetOverviewGroup.StandingBy;
    public bool IsFinished => Group == FleetOverviewGroup.Finished;

    /// <summary>The status cell: a filled dot for a started fleet, a hollow one standing by, a cross when finished.</summary>
    public string GroupStatusText => Group switch
    {
        FleetOverviewGroup.Active => "● ACTIVE",
        FleetOverviewGroup.Finished => "✕ DONE",
        _ => "○ READY",
    };

    /// <summary>The row is unfolded to its members. Remembered across reloads by the window, like the fold below.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Raised when the user folds or unfolds this row, so the window can remember it across a reload.</summary>
    public Action<FleetKey, bool>? RowExpansionChanged { get; set; }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        RowExpansionChanged?.Invoke(Key, IsExpanded);
    }

    /// <summary>"local" for a client-only fleet, the server's display name otherwise — where the fleet lives.</summary>
    public string OriginText => IsLocal ? "local" : (string.IsNullOrWhiteSpace(ServerName) ? ServerAddress ?? "server" : ServerName);

    /// <summary>The wide row's second line under the name: where it lives and who may join.</summary>
    public string KindText => $"{OriginText} · {VisibilityLabel.ToLowerInvariant()}";

    /// <summary>The narrow row's second line: where it lives, and what became of the "your characters" column
    /// (ET-170, screen 10) — the information never goes away, only its place does.</summary>
    public string NarrowSubText => OwnCharactersText == "—"
        ? KindText
        : $"{OriginText} · {OwnCharactersText.ToLowerInvariant()}{(OwnCharactersSubText is { Length: > 0 } sub ? $" · {sub}" : "")}";

    public string MemberCountText => MemberCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>Under the member count: how many of them are external pilots, or a dash.</summary>
    public string MemberCountSubText
    {
        get
        {
            int external = Members.Count(m => m.IsExternal);
            return external == 0 ? "—" : $"{external.ToString(CultureInfo.InvariantCulture)} external";
        }
    }

    /// <summary>The fleet commander's name, resolved from the roster, falling back to the owner label.</summary>
    public string CommanderText => Members.FirstOrDefault(m => m.IsFleetCommander)?.CharacterName ?? OwnerLabel;

    public string CommanderSubText => IsMine ? "you" : "not you";

    /// <summary>"3 linked" / "2 standing by" / "—": how many of this client's own characters this fleet holds and in
    /// what state — the column the narrow form folds into a second line under the name.</summary>
    [ObservableProperty] private string _ownCharactersText = "—";

    /// <summary>"1 elsewhere active" / the names standing by / "" — what qualifies the count above.</summary>
    [ObservableProperty] private string _ownCharactersSubText = "";

    partial void OnOwnCharactersTextChanged(string value) => OnPropertyChanged(nameof(NarrowSubText));
    partial void OnOwnCharactersSubTextChanged(string value) => OnPropertyChanged(nameof(NarrowSubText));

    public string DoctrineText => CompositionName ?? "—";

    /// <summary>The figure at the right: a running clock for a started fleet, the day it last ran while standing by.</summary>
    [ObservableProperty] private string _sinceText = "—";

    /// <summary>Under the figure: "since 20:14" for a started fleet, "last started 21:40" or "created 28-08" otherwise.</summary>
    [ObservableProperty] private string _sinceSubText = "";

    /// <summary>Recomputes the clock cell. Invariant on purpose — this is a clock and the tests run on a machine
    /// whose culture is not English (ET-34).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (IsInActiveGroup && Info.ActivatedAt is { } started)
        {
            var elapsed = now - started;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;
            SinceText = string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
            SinceSubText = string.Create(CultureInfo.InvariantCulture, $"since {started.ToLocalTime():HH:mm}");
            return;
        }

        if (IsInActiveGroup)
        {
            SinceText = "--:--:--";
            SinceSubText = "started, time unknown";
            return;
        }

        if (Info.ActivatedAt is { } last)
        {
            SinceText = string.Create(CultureInfo.InvariantCulture, $"{last.ToLocalTime():dd-MM}");
            SinceSubText = string.Create(CultureInfo.InvariantCulture, $"last started {last.ToLocalTime():HH:mm}");
            return;
        }

        SinceText = "—";
        SinceSubText = string.Create(CultureInfo.InvariantCulture, $"created {Info.CreatedAt.ToLocalTime():dd-MM}");
    }

    /// <summary>The window tells each row which of the two table states it is drawn in (ET-170): wide gives METRICS,
    /// SHARE and LEAVE their own buttons; narrow keeps STOP/START and MANAGE/VIEW and folds the rest behind "⋯" —
    /// two buttons plus an overflow, which is what scherm 10 has room for at 758.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMetricsButton))]
    [NotifyPropertyChangedFor(nameof(ShowShareButton))]
    [NotifyPropertyChangedFor(nameof(ShowLeave))]
    [NotifyPropertyChangedFor(nameof(ShowJoin))]
    [NotifyPropertyChangedFor(nameof(ShowRequest))]
    private bool _isWide;

    /// <summary>The width the actions cell is drawn at, handed down by the window — the budget JOIN has to fit in.</summary>
    [ObservableProperty] private double _actionsWidth = FleetOverviewLayout.MinActionsWidth;

    /// <summary>The secondary actions behind the "⋯" button, built by the window from what this row allows.</summary>
    public ObservableCollection<FleetMemberMenuItemViewModel> OverflowItems { get; } = [];

    public bool HasOverflow => OverflowItems.Count > 0;

    /// <summary>STOP shows on a started fleet. Only its owner may press it, so for anyone else it stands disabled
    /// rather than missing — a button that is not there teaches nobody who may press it.</summary>
    public bool ShowStop => IsInActiveGroup && (IsMine || IsParticipating);
    public bool StopEnabled => IsInActiveGroup && IsMine;

    public bool ShowStart => IsStandingBy && (IsMine || IsParticipating);
    public bool StartEnabled => IsStandingBy && IsMine;

    /// <summary>A member who is not the owner leaves rather than stops.</summary>
    public bool CanLeave => !IsFinished && IsParticipating && !IsMine;

    /// <summary>LEAVE keeps its place on the wide row (scherm 1) and moves behind "⋯" when the row is narrow.</summary>
    public bool ShowLeave => IsWide && CanLeave;

    /// <summary>A finished fleet has one thing left to do with it.</summary>
    public bool ShowDelete => IsFinished && IsMine;

    /// <summary>METRICS stands on the row of a started fleet: that is the fleet that has something to measure right
    /// now. A standing-by fleet keeps it behind "⋯" — scherm 1 gives its READY rows START, MANAGE and SHARE and no
    /// METRICS, which is also what keeps the wide row at the four buttons it draws.</summary>
    public bool ShowMetricsButton => IsWide && ShowMetricsActions && IsInActiveGroup;

    /// <summary>SHARE is the owner's switch — what this fleet's members share with each other. On someone else's
    /// fleet it is not mine to set, so it is not on the row (scherm 1: the Sansha row has no DEEL).</summary>
    public bool ShowShareButton => IsWide && IsMine && !IsFinished;

    /// <summary>My characters that are members of this fleet, shown as leaf rows under the fleet node (stream B / B-2):
    /// each with their role, assigned fit, can-fly badge and a SELECT FIT action. Since ET-170 this is the whole
    /// roster of every fleet, server ones included — the unfolded row shows the fleet commander and whoever asks for
    /// attention, and those are rarely mine. The row binds <see cref="VisibleMembers"/>, not this.</summary>
    public ObservableCollection<FleetMemberRowViewModel> Members { get; } = [];

    // --- The shortened member list (ET-53, re-cut by ET-170) ---

    /// <summary>
    /// What the unfolded row actually draws. Folded, it is the members that answer the three questions a row is
    /// unfolded for — are my pilots in it, do they count, and is anything wrong: the fleet commander, this client's
    /// own characters, and whoever is not linked or shares nothing. Everyone else is a tally on one line with a
    /// "show all N". So a fleet of fifty costs the height of a fleet of six, and the one member who silently does
    /// not take part can never disappear behind a click (ET-170, screen 12).
    /// </summary>
    public ObservableCollection<FleetMemberRowViewModel> VisibleMembers { get; } = [];

    /// <summary>The member list is unfolded. Kept across reloads by the fleets window, so removing a pilot from an
    /// unfolded 50-man list does not snap it shut (ET-52 meets ET-53).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreMembersLabel))]
    private bool _membersExpanded;

    /// <summary>Raised when the user folds or unfolds this card, so the window can remember it across a reload.</summary>
    public Action<long, bool>? MembersExpansionChanged { get; set; }

    /// <summary>A roster this size or smaller is drawn in full, externals included (screen 1: six pilots). Beyond it the
    /// row folds to the members that matter (screen 12), so a fleet of fifty costs the height of a fleet of six.</summary>
    public const int UnfoldedRosterLimit = 6;

    /// <summary>There are members beyond the highlighted ones and the roster is too long to draw whole — drives the
    /// summary line and its "show all N". False for a small fleet, or one whose every member is highlighted: no extra
    /// line and no extra click.</summary>
    public bool CanShortenMembers => Members.Count > UnfoldedRosterLimit && Members.Count > Members.Count(m => m.IsHighlighted);

    /// <summary>How many members the folded row leaves to the tally.</summary>
    public int HiddenMemberCount => CanShortenMembers ? Members.Count - Members.Count(m => m.IsHighlighted) : 0;

    /// <summary>The fold line's label: "show all 50" folded, "show fewer" once opened.</summary>
    public string MoreMembersLabel => MembersExpanded
        ? "show fewer"
        : string.Create(CultureInfo.InvariantCulture, $"show all {Members.Count}");

    /// <summary>"and 46 others — the fleet counts:" — the lead of the summary line under the highlighted members.</summary>
    public string HiddenMembersText
    {
        get
        {
            int hidden = HiddenMemberCount;
            return hidden == 1 ? "and 1 other — the fleet counts:" : string.Create(CultureInfo.InvariantCulture, $"and {hidden} others — the fleet counts:");
        }
    }

    /// <summary>The tallies on the summary line: linked · share nothing · offline · external, over the whole roster.
    /// Not decoration — see fifty members but 43 linked and you know the metrics are over 43 (ET-170).</summary>
    public ObservableCollection<FleetCountChipViewModel> HiddenMemberChips { get; } = [];

    /// <summary>"24 in fleet" — how many pilots the fleet holds, which is information in itself and therefore stays
    /// on the card whether the member list is folded or not.</summary>
    public string MemberCountLabel => $"{MemberCount} in fleet";

    /// <summary>Fold or unfold the member list. Inline rather than a jump to the roster window: the question "who
    /// else is in this fleet" is one the overview should answer where it is asked, and the card is the only place an
    /// external pilot appears at all. MANAGE/VIEW remains the route to the structure itself.</summary>
    [RelayCommand]
    private void ToggleMembers()
    {
        if (!CanShortenMembers)
            return;

        MembersExpanded = !MembersExpanded;
        RefreshVisibleMembers();
        MembersExpansionChanged?.Invoke(Id, MembersExpanded);
    }

    /// <summary>
    /// Who the list shows first: the fleet commander, then this client's own characters, then whoever asks for
    /// attention, then external pilots, then everyone else in roster order. Listing the first few off the roster is
    /// the one ordering that answers nobody's question.
    /// </summary>
    private static int Rank(FleetMemberRowViewModel member) =>
        member.IsFleetCommander ? 0
        : member.IsMine ? 1
        : member.NeedsAttention ? 2
        : member.IsExternal ? 3
        : 4;

    /// <summary>Rebuilds <see cref="VisibleMembers"/> from <see cref="Members"/>. Called by the loader once the leaves
    /// are in, by the overview rebuild once every member's link state is known, and by the fold toggle.</summary>
    public void RefreshVisibleMembers()
    {
        var ordered = Members.OrderBy(Rank).ToList();   // stable: roster order survives inside a rank
        var shown = MembersExpanded || !CanShortenMembers
            ? ordered
            : ordered.Where(m => m.IsHighlighted).ToList();

        VisibleMembers.Clear();
        foreach (var member in shown)
            VisibleMembers.Add(member);

        HiddenMemberChips.Clear();
        int linked = Members.Count(m => m.LinkState == FleetMemberLinkState.Linked);
        int sharesNothing = Members.Count(m => m.SharesNothing);
        int offline = Members.Count(m => m.IsOffline);
        int external = Members.Count(m => m.IsExternal);
        int elsewhere = Members.Count(m => m.IsElsewhereActive);
        if (IsInActiveGroup)
            HiddenMemberChips.Add(new(Count(linked, "linked")));
        if (elsewhere > 0)
            HiddenMemberChips.Add(new(Count(elsewhere, "elsewhere active"), FleetChipTone.Warn));
        if (sharesNothing > 0)
            HiddenMemberChips.Add(new(sharesNothing == 1 ? "1 shares nothing" : Count(sharesNothing, "share nothing"), FleetChipTone.Warn));
        if (offline > 0)
            HiddenMemberChips.Add(new(Count(offline, "offline")));
        if (external > 0)
            HiddenMemberChips.Add(new(Count(external, "external")));

        OnPropertyChanged(nameof(CanShortenMembers));
        OnPropertyChanged(nameof(HiddenMemberCount));
        OnPropertyChanged(nameof(HiddenMembersText));
        OnPropertyChanged(nameof(MoreMembersLabel));
        OnPropertyChanged(nameof(MemberCountSubText));
        OnPropertyChanged(nameof(CommanderText));
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}");

    /// <summary>The doctrine coupled to this fleet, shown as a pill on the node; null when none is coupled.</summary>
    [ObservableProperty] private string? _compositionName;

    /// <summary>Drives the doctrine pill's visibility — only shown when a composition is coupled.</summary>
    public bool HasComposition => !string.IsNullOrEmpty(CompositionName);

    partial void OnCompositionNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasComposition));
        OnPropertyChanged(nameof(DoctrineText));
    }

    /// <summary>Per-role doctrine fill for the discoverable browser card (stream B / B-1): a "DPS 24 / 40" pill per
    /// role-group with a minimum, so a pilot sees how full each role already is without joining. Empty when the fleet
    /// has no coupled composition (fill, computed via <see cref="CompositionFillBuilder"/>).</summary>
    public ObservableCollection<CompositionFillRoleViewModel> RoleFill { get; } = [];

    /// <summary>Live member count for the browser card ("24 in fleet") — the whole fleet, not the leaves the card
    /// happens to list, so the total stays visible however short the list is (ET-53).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemberCountLabel))]
    [NotifyPropertyChangedFor(nameof(MemberCountText))]
    private int _memberCount;

    // ── Unified-overview state: set by the loader after the per-server merge so one fleet row
    // carries every relationship at once — owned, joined, and/or discoverable — instead of living in three tabs. ──

    /// <summary>Client-only fleet: no coupled server, so it shows local-only actions.</summary>
    public bool IsLocal => ServerAddress is null;

    /// <summary>At least one of my coupled characters is a member of this fleet (joined). Drives the read-only
    /// VIEW button, the member leaves, LEAVE and the metrics/sharing actions.</summary>
    [ObservableProperty] private bool _isParticipating;

    /// <summary>The fleet showed up in the discoverable (open) list, so JOIN/REQUEST applies in principle — even when
    /// every one of my characters is already in (then the button shows disabled, see <see cref="JoinEnabled"/>).</summary>
    [ObservableProperty] private bool _isDiscoverable;

    /// <summary>I have a coupled character on this server that is not yet a member — so a join/request can still go
    /// through (also when another of my characters is already in). Drives the enabled state of JOIN/REQUEST.</summary>
    [ObservableProperty] private bool _canJoinHere;

    /// <summary>The creator's character name, resolved best-effort for fleets I don't own.</summary>
    [ObservableProperty] private string _ownerName = "";

    /// <summary>The owner's character name — used everywhere as "Owner: {name}" (server + local, one format). My own
    /// fleets show my owning character's name; other fleets the resolved creator name (id fallback until resolved).</summary>
    public string OwnerLabel =>
        IsMine ? (string.IsNullOrWhiteSpace(CharacterName) ? "you" : CharacterName)
        : string.IsNullOrWhiteSpace(OwnerName) ? $"char {Info.CreatorCharacterId}" : OwnerName;

    /// <summary>A fleet I have any relationship with can take another of my characters: a discoverable one I can join,
    /// or one I already own/participate in where another alt is still free.</summary>
    private bool CanAddCharacter => IsMine || IsParticipating || IsDiscoverable;

    /// <summary>JOIN (public) / REQUEST (invite-only) apply to any fleet I relate to — including one I own or already
    /// fly with one character — so I can bring another alt in. They only disable when no character is free, so "all my
    /// characters are already in" reads as a greyed-out action and not a missing one.</summary>
    public bool CanJoin => IsPublic && CanAddCharacter && !IsLocal && !IsFinished;
    public bool CanRequest => IsInviteOnly && CanAddCharacter && !IsLocal && !IsFinished;

    /// <summary>
    /// Whether JOIN / REQUEST got a place on the row this time. Set by the window, which is the only party that
    /// knows both what else the row is drawing and what the "⋯" would otherwise hold — see
    /// <c>FleetsViewModel.BuildOverflow</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJoin))]
    [NotifyPropertyChangedFor(nameof(ShowRequest))]
    private bool _joinOnRow;

    /// <summary>
    /// On the row whenever the row has the width for it (Jithran, 2026-09-04: "als het past zou de join in de rij het
    /// beste zijn"). Greyed-out rather than one click deeper is the whole point, so the button carries the reason it
    /// is off in a tooltip that shows while disabled. At 758 the row has room for two buttons and an overflow —
    /// scherm 10 and scherm 15 both say so — and there JOIN folds into the menu, reason and all.
    /// </summary>
    public bool ShowJoin => IsWide && CanJoin && JoinOnRow;
    public bool ShowRequest => IsWide && CanRequest && JoinOnRow;
    public bool JoinEnabled => CanJoinHere;

    /// <summary>What JOIN or REQUEST would cost this row, whichever of the two it is; 0 when neither applies.</summary>
    public double JoinActionWidth => CanRequest ? FleetRowActionWidths.Request : CanJoin ? FleetRowActionWidths.Join : 0;

    /// <summary>
    /// What the buttons that always stand on this row already take. These are the ones scherm 1 draws, and none of
    /// them may be pushed off to make room for JOIN — so this sum is the budget JOIN has to fit beside.
    /// </summary>
    public double StandingActionsWidth
    {
        get
        {
            double width = 0;
            if (ShowStop)
                width += FleetRowActionWidths.Stop;
            if (ShowStart)
                width += FleetRowActionWidths.Start;
            if (ShowRosterButton)
                width += IsMine ? FleetRowActionWidths.Manage : FleetRowActionWidths.View;
            if (ShowMetricsButton)
                width += FleetRowActionWidths.Metrics;
            if (ShowShareButton)
                width += FleetRowActionWidths.Share;
            if (ShowLeave)
                width += FleetRowActionWidths.Leave;
            if (ShowDelete)
                width += FleetRowActionWidths.Delete;
            return width;
        }
    }

    /// <summary>Why JOIN / REQUEST is off, or what it does when it is on — the same sentence the overflow line
    /// carries, so the answer does not depend on where the action happens to be drawn.</summary>
    public string JoinHint => JoinEnabled
        ? "Bring one or more of your characters into this fleet"
        : "Every one of your characters on this server is already in";

    /// <summary>Owner → MANAGE (full roster), member → VIEW (same roster, read-only structure + assigned fits).</summary>
    public bool ShowRosterButton => IsMine || IsParticipating;
    public string RosterButtonLabel => IsMine ? "MANAGE" : "VIEW";

    /// <summary>Owner-only management on a server fleet: EDIT + DISBAND.</summary>
    public bool ShowOwnerActions => IsMine && !IsLocal;

    /// <summary>Metrics + per-fleet sharing apply once I'm in the fleet (owner or member).</summary>
    public bool ShowMetricsActions => IsMine || IsParticipating;

    partial void OnIsParticipatingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRosterButton));
        OnPropertyChanged(nameof(ShowMetricsActions));
        OnPropertyChanged(nameof(ShowMetricsButton));
        OnPropertyChanged(nameof(ShowShareButton));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanRequest));
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(ShowRequest));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(CanLeave));
        OnPropertyChanged(nameof(ShowLeave));
    }

    partial void OnIsDiscoverableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanRequest));
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(ShowRequest));
    }

    partial void OnCanJoinHereChanged(bool value)
    {
        OnPropertyChanged(nameof(JoinEnabled));
        OnPropertyChanged(nameof(JoinHint));
    }

    partial void OnOwnerNameChanged(string value)
    {
        OnPropertyChanged(nameof(OwnerLabel));
        OnPropertyChanged(nameof(CommanderText));
    }

    /// <summary>Tells the row its overflow menu changed, so "⋯" appears or goes.</summary>
    public void OverflowChanged() => OnPropertyChanged(nameof(HasOverflow));
}
