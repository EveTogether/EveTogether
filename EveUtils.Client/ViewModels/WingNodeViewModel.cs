using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A wing node in the roster tree. The wing commander is shown in the node header (E1, "Wing X (n) ·
/// WC: Name"); <see cref="Children"/> mixes <see cref="SquadNodeViewModel"/>s and any wing-level member rows. The
/// right-click menu (E2) adds/deletes a squad, invites/assigns a member at wing level, deletes the wing when empty,
/// and — when a commander is present — moves or removes that wing commander through the reused <see cref="Commander"/>.
/// </summary>
public sealed class WingNodeViewModel
{
    public WingNodeViewModel(
        long wingId, string label, bool isOwner,
        IAsyncRelayCommand addSquadCommand, IAsyncRelayCommand inviteHereCommand, IAsyncRelayCommand assignHereCommand,
        IAsyncRelayCommand renameCommand, IAsyncRelayCommand deleteCommand, bool canDelete,
        MemberNodeViewModel? commander)
    {
        WingId = wingId;
        Label = label;
        IsOwner = isOwner;
        AddSquadCommand = addSquadCommand;
        InviteHereCommand = inviteHereCommand;
        AssignHereCommand = assignHereCommand;
        RenameCommand = renameCommand;
        DeleteCommand = deleteCommand;
        CanDelete = canDelete;
        Commander = commander;
    }

    public long WingId { get; }
    public string Label { get; }
    public bool IsOwner { get; }

    /// <summary>Kept expanded across roster reloads so an action doesn't collapse the tree (R3-3).</summary>
    public bool IsExpanded { get; } = true;

    public IAsyncRelayCommand AddSquadCommand { get; }
    public IAsyncRelayCommand InviteHereCommand { get; }
    public IAsyncRelayCommand AssignHereCommand { get; }

    /// <summary>Renames this wing; for a coupled fleet it also renames the in-game wing.</summary>
    public IAsyncRelayCommand RenameCommand { get; }

    /// <summary>Deletes this wing (R3-1). Only offered when the wing is empty (no squads, no members).</summary>
    public IAsyncRelayCommand DeleteCommand { get; }
    public bool CanDelete { get; }

    /// <summary>The wing commander as a member node, so the menu reuses its move/remove cascade; null if none yet.</summary>
    public MemberNodeViewModel? Commander { get; }
    public bool HasCommander => Commander is not null;
    public bool CommanderHasFit => Commander?.HasAssignedFit ?? false;
    public bool CommanderCanFly => Commander?.CanFly ?? false;
    public bool CommanderHasSkillGap => Commander?.HasSkillGap ?? false;
    public string CommanderSkillBadgeTooltip => Commander?.SkillBadgeTooltip ?? string.Empty;

    /// <summary>Squad nodes followed by wing-level member rows (squad id -1, non-commander).</summary>
    public ObservableCollection<object> Children { get; } = [];
}
