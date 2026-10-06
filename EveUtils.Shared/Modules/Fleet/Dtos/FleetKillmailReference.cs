namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>The immutable ESI identity and fleet-facing facts of one shared killmail.</summary>
public sealed class FleetKillmailReference
{
    public int KillmailId { get; init; }
    public required string Hash { get; init; }
    public DateTime KillmailTimeUtc { get; init; }
    public bool IsLoss { get; init; }
}
