using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The fleet root node in the roster tree. The fleet commander is shown in the node header (E1,
/// "Fleet (N) · FC: Name"); <see cref="Children"/> mixes the wing nodes and any fleet-level member rows (wing id -1,
/// non-FC). The right-click menu (E2) adds a wing, invites/assigns a member at fleet level, and — when a fleet
/// commander is present — moves or removes them through the reused <see cref="Commander"/> member node.
/// </summary>
public sealed class FleetRootNodeViewModel
{
    public FleetRootNodeViewModel(
        string label, bool isOwner,
        IAsyncRelayCommand addWingCommand, IAsyncRelayCommand inviteHereCommand, IAsyncRelayCommand assignHereCommand,
        MemberNodeViewModel? commander)
    {
        Label = label;
        IsOwner = isOwner;
        AddWingCommand = addWingCommand;
        InviteHereCommand = inviteHereCommand;
        AssignHereCommand = assignHereCommand;
        Commander = commander;
    }

    public string Label { get; }
    public bool IsOwner { get; }

    /// <summary>Kept expanded across roster reloads so an action doesn't collapse the tree (R3-3).</summary>
    public bool IsExpanded { get; } = true;

    public IAsyncRelayCommand AddWingCommand { get; }
    public IAsyncRelayCommand InviteHereCommand { get; }
    public IAsyncRelayCommand AssignHereCommand { get; }

    /// <summary>The fleet commander as a member node, so the menu reuses its move/remove cascade; null if none yet.</summary>
    public MemberNodeViewModel? Commander { get; }
    public bool HasCommander => Commander is not null;
    public bool CommanderHasFit => Commander?.HasAssignedFit ?? false;
    public bool CommanderCanFly => Commander?.CanFly ?? false;
    public bool CommanderHasSkillGap => Commander?.HasSkillGap ?? false;
    public string CommanderSkillBadgeTooltip => Commander?.SkillBadgeTooltip ?? string.Empty;

    /// <summary>Wing nodes followed by fleet-level member rows (wing id -1, non-FC).</summary>
    public ObservableCollection<object> Children { get; } = [];
}
