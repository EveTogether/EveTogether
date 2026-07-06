using System.Collections.Generic;
using System.Windows.Input;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One entry in the EVE-style "move to…" cascade menu. A branch (<see cref="Children"/> non-empty,
/// <see cref="Command"/> null) opens a submenu — Fleet → Wing → Squad; a leaf carries the command that moves the
/// target member onto that exact role + wing + squad. The position dictates the role, mirroring the in-game fleet UI
/// (a member is never simultaneously fleet/wing/squad leader — the chosen leaf is a single level).
/// </summary>
public sealed class MoveTargetViewModel
{
    public MoveTargetViewModel(string label, IReadOnlyList<MoveTargetViewModel> children, ICommand? command)
    {
        Label = label;
        Children = children;
        Command = command;
    }

    public string Label { get; }
    public IReadOnlyList<MoveTargetViewModel> Children { get; }
    public ICommand? Command { get; }
}
