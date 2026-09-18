namespace EveUtils.Server.DataExplorer;

/// <summary>A composition whose entries link to a shared fit, and in which of its roles.</summary>
public sealed class SharedFitCompositionUse
{
    public required long CompositionId { get; init; }
    public required string CompositionName { get; init; }
    public required IReadOnlyList<string> RoleNames { get; init; }
}
