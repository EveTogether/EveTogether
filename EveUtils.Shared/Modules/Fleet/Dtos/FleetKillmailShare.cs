namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>One pilot's complete current killmail share for a fleet.</summary>
public sealed class FleetKillmailShare
{
    public long FleetId { get; init; }
    public long UnixMs { get; init; }
    public IReadOnlyList<FleetKillmailReference> Killmails { get; init; } = [];
}
