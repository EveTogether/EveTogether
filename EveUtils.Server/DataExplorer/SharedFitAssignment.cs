namespace EveUtils.Server.DataExplorer;

/// <summary>A fleet member who has a shared fit assigned.</summary>
public sealed class SharedFitAssignment
{
    public required long FleetId { get; init; }
    public required string FleetName { get; init; }
    public required int CharacterId { get; init; }
}
