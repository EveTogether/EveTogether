namespace EveUtils.Server.DataExplorer;

/// <summary>One allowed fit within a role, with the members assigned to it and its own minimum, if it has one.</summary>
public sealed class EntryCoverage
{
    public required string FitName { get; init; }
    public required int ShipTypeId { get; init; }
    public int? ServerSharedFitId { get; init; }
    public required int Assigned { get; init; }
    public int? Minimum { get; init; }
}
