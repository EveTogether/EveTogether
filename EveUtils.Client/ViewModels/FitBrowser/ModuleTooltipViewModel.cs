using System.Collections.Generic;
using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>The in-game per-module tooltip: the module name, its loaded charge, the
/// type-specific derived lines, a colour-coded damage-type breakdown and a state line coloured by the module's state.
/// Built per slot from its <see cref="EveUtils.Shared.Modules.Dogma.ModuleContribution"/>.</summary>
public sealed class ModuleTooltipViewModel
{
    public required string Name { get; init; }
    /// <summary>The slot's category + ordinal ("HIGH 1", "MID 2", …) standing in for the in-game slot hotkey, which we
    /// have no equivalent for; empty when the slot has no position assigned.</summary>
    public string SlotLabel { get; init; } = "";
    public bool HasSlotLabel => SlotLabel.Length > 0;
    public string? ChargeName { get; init; }
    public bool HasCharge => ChargeName is not null;
    public IReadOnlyList<string> Lines { get; init; } = [];
    public IReadOnlyList<DamageSegmentViewModel> DamageSegments { get; init; } = [];
    public bool HasDamage => DamageSegments.Count > 0;
    public required string StateLabel { get; init; }
    public required IBrush StateBrush { get; init; }
}
