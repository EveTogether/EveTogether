using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A squad node in the roster tree. The squad commander is shown in the node header (E1, "Squad Y (n) ·
/// SC: Name"); the squad's other members hang underneath as <see cref="Members"/>. The right-click menu (E2) invites
/// or assigns a member into this squad, deletes the squad when empty, and — when a commander is present — moves or
/// removes that commander via the same cascade as a member row (reusing the <see cref="Commander"/> member node).
/// </summary>
public sealed class SquadNodeViewModel
{
    public SquadNodeViewModel(
        long squadId, long wingId, string label, bool isOwner,
        IAsyncRelayCommand inviteHereCommand, IAsyncRelayCommand assignHereCommand,
        IAsyncRelayCommand renameCommand, IAsyncRelayCommand deleteCommand, bool canDelete,
        MemberNodeViewModel? commander)
    {
        SquadId = squadId;
        WingId = wingId;
        Label = label;
        IsOwner = isOwner;
        InviteHereCommand = inviteHereCommand;
        AssignHereCommand = assignHereCommand;
        RenameCommand = renameCommand;
        DeleteCommand = deleteCommand;
        CanDelete = canDelete;
        Commander = commander;
    }

    public long SquadId { get; }

    /// <summary>The wing this squad belongs to — carried so a drag-drop onto the squad knows its full position
    /// (role + wing + squad) for the move (stream G / G-3).</summary>
    public long WingId { get; }

    public string Label { get; }
    public bool IsOwner { get; }

    /// <summary>Kept expanded across roster reloads so an action doesn't collapse the tree (R3-3).</summary>
    public bool IsExpanded { get; } = true;

    /// <summary>Invites a connected character (role chosen in the dialog) into this squad's wing/squad position.</summary>
    public IAsyncRelayCommand InviteHereCommand { get; }

    /// <summary>Assigns an already-accepted member into this squad as a Squad Member.</summary>
    public IAsyncRelayCommand AssignHereCommand { get; }

    /// <summary>Renames this squad; for a coupled fleet it also renames the in-game squad.</summary>
    public IAsyncRelayCommand RenameCommand { get; }

    /// <summary>Deletes this squad (R3-1). Only offered when the squad is empty (no members).</summary>
    public IAsyncRelayCommand DeleteCommand { get; }
    public bool CanDelete { get; }

    /// <summary>The squad commander as a member node, so the menu reuses its move/remove cascade; null if none yet.</summary>
    public MemberNodeViewModel? Commander { get; }
    public bool HasCommander => Commander is not null;
    public bool CommanderHasFit => Commander?.HasAssignedFit ?? false;
    public bool CommanderCanFly => Commander?.CanFly ?? false;
    public bool CommanderHasSkillGap => Commander?.HasSkillGap ?? false;
    public string CommanderSkillBadgeTooltip => Commander?.SkillBadgeTooltip ?? string.Empty;

    public ObservableCollection<MemberNodeViewModel> Members { get; } = [];
}
