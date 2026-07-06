using System.Collections.Generic;
using System.Windows.Input;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One entry in a module's right-click menu: a label plus either a command (a leaf such as Information,
/// Remove charge or a specific charge) or child entries (a submenu such as "Charges").</summary>
public sealed class ChargeMenuOptionViewModel(
    string label, ICommand? command = null, IReadOnlyList<ChargeMenuOptionViewModel>? children = null)
{
    public string Label { get; } = label;
    public ICommand? Command { get; } = command;
    public IReadOnlyList<ChargeMenuOptionViewModel>? Children { get; } = children;
}
