using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
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

    /// <summary>My characters that are members of this fleet, shown as leaf rows under the fleet node (stream B / B-2):
    /// each with their role, assigned fit, can-fly badge and a SELECT FIT action. Empty for browser rows
    /// (discoverable ≠ joined). The same fleet is listed once and aggregates all my coupled characters in it.
    /// A client-only fleet's card carries the WHOLE roster instead, externals included (ET-46) — which is why this
    /// list needs shortening at all. The card binds <see cref="VisibleMembers"/>, not this.</summary>
    public ObservableCollection<FleetMemberRowViewModel> Members { get; } = [];

    // --- The shortened member list (ET-53) ---

    /// <summary>
    /// How many member leaves a card shows before it shortens the list. Six rather than the five first suggested:
    /// the ranking below puts the fleet commander first and this client's own characters next, and a multiboxer
    /// flying four alts plus the FC already fills five — with six there is still a slot left for the first pilot who
    /// is neither, which is the whole reason a shortened list is worth looking at. Beyond that the card stops being
    /// an overview: at ~40px a leaf, six is roughly a third of the window's height for one of possibly several cards.
    /// </summary>
    public const int CollapsedMemberLimit = 6;

    /// <summary>What the card actually draws: <see cref="Members"/> in full when unfolded, otherwise the first
    /// <see cref="CollapsedMemberLimit"/> of them — which the loader has ordered fleet commander first, then this
    /// client's own characters, then external pilots, then the rest.</summary>
    public ObservableCollection<FleetMemberRowViewModel> VisibleMembers { get; } = [];

    /// <summary>The member list is unfolded. Kept across reloads by the fleets window, so removing a pilot from an
    /// unfolded 50-man list does not snap it shut (ET-52 meets ET-53).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreMembersLabel))]
    private bool _membersExpanded;

    /// <summary>Raised when the user folds or unfolds this card, so the window can remember it across a reload.</summary>
    public Action<long, bool>? MembersExpansionChanged { get; set; }

    /// <summary>There are more members than a folded card shows — drives the "+ n more" line. False for a small
    /// fleet, which then has no extra line and no extra click at all.</summary>
    public bool CanShortenMembers => Members.Count > CollapsedMemberLimit;

    /// <summary>How many members the folded card leaves out, and how many of those are external pilots. An external
    /// has a row on this card and nowhere else in the client (ET-46), so a hidden one is worth naming rather than
    /// counting silently — it is what tells the FC there is something under the line they cannot see elsewhere.</summary>
    private int HiddenMemberCount => Math.Max(0, Members.Count - CollapsedMemberLimit);

    private int _hiddenExternalCount;

    /// <summary>The fold line's label. "+ 45 more" on a client-only card, whose list is the whole roster; "+ 2 more of
    /// yours" on a server card, whose list is only my own characters, so the number never reads as the fleet's size.</summary>
    public string MoreMembersLabel
    {
        get
        {
            if (MembersExpanded)
                return "▴ SHOW FEWER";

            string subject = IsLocal ? "more" : "more of yours";
            return _hiddenExternalCount > 0
                ? $"▾ + {HiddenMemberCount} {subject} · {_hiddenExternalCount} external"
                : $"▾ + {HiddenMemberCount} {subject}";
        }
    }

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

    /// <summary>Rebuilds <see cref="VisibleMembers"/> from <see cref="Members"/>. Called by the loader once the leaves
    /// are in, and by the fold toggle.</summary>
    public void RefreshVisibleMembers()
    {
        var shown = MembersExpanded || !CanShortenMembers
            ? (IReadOnlyList<FleetMemberRowViewModel>)Members
            : Members.Take(CollapsedMemberLimit).ToList();

        VisibleMembers.Clear();
        foreach (var member in shown)
            VisibleMembers.Add(member);

        _hiddenExternalCount = Members.Skip(CollapsedMemberLimit).Count(m => m.IsExternal);
        OnPropertyChanged(nameof(CanShortenMembers));
        OnPropertyChanged(nameof(MoreMembersLabel));
    }

    /// <summary>The doctrine coupled to this fleet, shown as a pill on the node; null when none is coupled.</summary>
    [ObservableProperty] private string? _compositionName;

    /// <summary>Drives the doctrine pill's visibility — only shown when a composition is coupled.</summary>
    public bool HasComposition => !string.IsNullOrEmpty(CompositionName);

    partial void OnCompositionNameChanged(string? value) => OnPropertyChanged(nameof(HasComposition));

    /// <summary>Per-role doctrine fill for the discoverable browser card (stream B / B-1): a "DPS 24 / 40" pill per
    /// role-group with a minimum, so a pilot sees how full each role already is without joining. Empty when the fleet
    /// has no coupled composition (fill, computed via <see cref="CompositionFillBuilder"/>).</summary>
    public ObservableCollection<CompositionFillRoleViewModel> RoleFill { get; } = [];

    /// <summary>Live member count for the browser card ("24 in fleet") — the whole fleet, not the leaves the card
    /// happens to list, so the total stays visible however short the list is (ET-53).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemberCountLabel))]
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

    /// <summary>JOIN (public) / REQUEST (invite-only) stay visible for any fleet I relate to — including one I own or
    /// already fly with one character — so I can bring another alt in. They only disable when no character is free
    /// (so "all my characters are already in" reads as a greyed-out button, not a missing one).</summary>
    public bool ShowJoin => IsPublic && CanAddCharacter;
    public bool ShowRequest => IsInviteOnly && CanAddCharacter;
    public bool JoinEnabled => CanJoinHere;

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
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(ShowRequest));
    }

    partial void OnIsDiscoverableChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowJoin));
        OnPropertyChanged(nameof(ShowRequest));
    }

    partial void OnCanJoinHereChanged(bool value) => OnPropertyChanged(nameof(JoinEnabled));

    partial void OnOwnerNameChanged(string value) => OnPropertyChanged(nameof(OwnerLabel));
}
