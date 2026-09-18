using EveUtils.Server.Esi;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.ServerAuth.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>One paired character with everything on this server that hangs off it.</summary>
public sealed class CharacterListItem
{
    public required SyncedCharacter Character { get; init; }
    public required IReadOnlyList<SessionListItem> Sessions { get; init; }
    public required IReadOnlyList<CharacterFleetSeat> Fleets { get; init; }
    public required IReadOnlyList<SharedFit> SharedFits { get; init; }
    public required IReadOnlyList<FleetComposition> OwnedCompositions { get; init; }

    public DateTimeOffset? LastSeen => Sessions.Count == 0 ? null : Sessions.Max(s => s.Session.LastHeartbeat);

    public bool IsTokenRevoked => Character.FailureCount == ServerTokenRefreshService.RevokedFailureCount;

    public CharacterPanelStatus Status =>
        Character.FailureCount > 0 ? CharacterPanelStatus.Failing
        : Sessions.Count == 0 ? CharacterPanelStatus.NeverConnected
        : Sessions.Any(s => s.Status == SessionPanelStatus.Live) ? CharacterPanelStatus.Live
        : CharacterPanelStatus.Idle;
}
