using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A non-commander member leaf in the fleet roster tree. The level commanders are shown in their node's
/// header (E1); this row is an ordinary squad/wing/fleet member (or an unassigned one — R3-5). Carries the EVE-cascade
/// <see cref="MoveTargets"/> plus the unassign/remove/transfer commands so the right-click context menu (E2) acts
/// directly on this member. The same instance also backs a level commander shown in a node header, so the node menu
/// can reuse its move/unassign/cascade.
/// </summary>
public sealed partial class MemberNodeViewModel : ObservableObject, IFleetMemberMenuHost
{
    private readonly MemberSkillBadge? _skillBadge;

    public MemberNodeViewModel(
        FleetMemberInfo member, string displayName, bool isOwner,
        IReadOnlyList<MoveTargetViewModel> moveTargets,
        IAsyncRelayCommand unassignCommand, IAsyncRelayCommand removeFromFleetCommand,
        IAsyncRelayCommand transferOwnershipCommand, IAsyncRelayCommand assignFitCommand,
        IAsyncRelayCommand openFitCommand, MemberSkillBadge? skillBadge = null,
        string? shipName = null)
    {
        Member = member;
        IsOwner = isOwner;
        MoveTargets = moveTargets;
        UnassignCommand = unassignCommand;
        RemoveFromFleetCommand = removeFromFleetCommand;
        TransferOwnershipCommand = transferOwnershipCommand;
        AssignFitCommand = assignFitCommand;
        OpenFitCommand = openFitCommand;
        // A local skill evaluation wins; without one (this client doesn't know the pilot's skills) the
        // pilot-reported wire verdict backs the badge — so a remote member shows the badge too.
        _skillBadge = skillBadge ?? member.FitSkillVerdict switch
        {
            FitSkillVerdict.CanFly => new MemberSkillBadge(CanFly: true, "Can fly this fit (reported by the pilot's client)"),
            FitSkillVerdict.MissingSkills => new MemberSkillBadge(CanFly: false, "Missing skills (reported by the pilot's client)"),
            _ => null
        };

        var roleLabel = member.Role switch
        {
            FleetRole.FleetCommander => "FC",
            FleetRole.WingCommander => "WC",
            FleetRole.SquadCommander => "SC",
            FleetRole.Unassigned => "Unassigned",
            _ => "Member"
        };
        var externalSuffix = member.IsExternal ? " (extern)" : string.Empty;
        Label = $"{displayName} — {roleLabel}{externalSuffix}";

        DisplayName = displayName;
        _shipName = shipName;
        MemberMenu = _BuildMenu();
    }

    private readonly string? _shipName;

    /// <summary>The pilot's name on its own, without the role suffix <see cref="Label"/> carries.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Whether this pilot is here, kept current by the roster's own sweep (ET-70). Observable because it moves while
    /// the window stands open — a member closing their client is news that arrives as nothing arriving, and the tree
    /// is otherwise only rebuilt when the roster itself changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private FleetMemberPresenceState _presence = FleetMemberPresenceState.Unknown;

    /// <summary>Known not to be here — an own category, distinct from a pilot nothing is known about.</summary>
    public bool IsOffline => Presence is FleetMemberPresenceState.Offline;

    // The menu carries the presence line, so it is rebuilt when the verdict moves rather than showing the reading
    // from whenever the tree happened to be built.
    partial void OnPresenceChanged(FleetMemberPresenceState value) => MemberMenu = _BuildMenu();

    /// <summary>The shared fleet-member information block (ET-44) — the roster's facts, no live metrics.</summary>
    [ObservableProperty] private IReadOnlyList<FleetMemberMenuItemViewModel> _memberMenu;

    // The same pilot summary fleet metrics shows, built from the same place — in the view it hangs under a "Pilot
    // details" submenu because this menu already carries the roster's own structure actions. No remove action:
    // the roster menu has had its own "Remove from fleet" since E2, and it runs the same shared flow.
    private IReadOnlyList<FleetMemberMenuItemViewModel> _BuildMenu() => FleetMemberMenu.Build(
        new FleetMemberFacts(DisplayName, Member.Role, Member.IsExternal, _shipName, Member.AssignedFit?.FitName,
            Presence: Presence),
        DateTimeOffset.UtcNow);

    public FleetMemberInfo Member { get; }
    public string Label { get; }
    public bool IsOwner { get; }

    /// <summary>Leaf node — no children to expand; present so the shared TreeViewItem expansion style binds cleanly.</summary>
    public bool IsExpanded => false;

    /// <summary>The EVE cascade (Fleet → Wing → Squad) that moves this member onto the chosen position.</summary>
    public IReadOnlyList<MoveTargetViewModel> MoveTargets { get; }

    /// <summary>"Remove from squad" (R3-5): drops the member to unassigned — they stay in the fleet.</summary>
    public IAsyncRelayCommand UnassignCommand { get; }

    /// <summary>"Remove from fleet": actually removes the member from the fleet (kick).</summary>
    public IAsyncRelayCommand RemoveFromFleetCommand { get; }

    public IAsyncRelayCommand TransferOwnershipCommand { get; }

    /// <summary>Assigns/changes the fit this member flies through the single fit picker.</summary>
    public IAsyncRelayCommand AssignFitCommand { get; }

    /// <summary>Opens the read-only radial fit-detail of this member's assigned fit.</summary>
    public IAsyncRelayCommand OpenFitCommand { get; }

    public bool HasAssignedFit => Member.AssignedFit is not null;

    /// <summary>The assigned fit's name, or a placeholder when none is assigned.</summary>
    public string AssignedFitLabel => Member.AssignedFit?.FitName ?? "— no fit —";

    public string AssignFitButtonLabel => HasAssignedFit ? "CHANGE FIT" : "PICK FIT";

    // --- skill-gap badge: can-fly / missing skills; neither when there is no verdict. ---

    /// <summary>The pilot trains every skill the assigned fit needs.</summary>
    public bool CanFly => _skillBadge is { CanFly: true };

    /// <summary>The assigned fit needs skills the pilot lacks.</summary>
    public bool HasSkillGap => _skillBadge is { CanFly: false };

    public string SkillBadgeTooltip => _skillBadge?.Tooltip ?? string.Empty;
}
