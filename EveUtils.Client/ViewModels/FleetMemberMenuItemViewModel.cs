using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One line of the shared fleet-member context menu. An information line carries no command and reads as a
/// disabled row; an action carries one. Keeping the menu data rather than markup is what lets the whole menu be
/// defined once (<see cref="FleetMemberMenu"/>) instead of once per screen that shows a member — the
/// <c>FleetMemberMenuItemTheme</c> in <c>App.axaml</c> maps these onto the <c>MenuItem</c>s.
/// </summary>
public sealed class FleetMemberMenuItemViewModel(string label, IRelayCommand? command = null, string? tooltip = null)
{
    public string Label { get; } = label;

    /// <summary>Null for an information line — that is also what disables it.</summary>
    public IRelayCommand? Command { get; } = command;

    public string? Tooltip { get; } = tooltip;

    public bool IsEnabled => Command is not null;
}
