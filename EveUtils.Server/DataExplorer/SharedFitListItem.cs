using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>One shared fit with the compositions and fleet members that took a copy of it.</summary>
public sealed class SharedFitListItem
{
    public required SharedFit Fit { get; init; }
    public required IReadOnlyList<SharedFitCompositionUse> Compositions { get; init; }
    public required IReadOnlyList<SharedFitAssignment> Assignments { get; init; }

    public bool IsUsed => Compositions.Count > 0 || Assignments.Count > 0;
}
