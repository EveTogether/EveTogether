namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// Whether an internal fleet is bound to a live in-game ESI fleet. Default <see cref="NotLinked"/>; set to
/// <see cref="Linked"/> once a coupled character's <c>GET /characters/{id}/fleet/</c> resolves the in-game fleet_id
/// onto this fleet. Open for extension — finer sync states can be added without a schema redesign.
/// </summary>
public enum EsiFleetSyncState
{
    NotLinked = 0,
    Linked = 1,
}
