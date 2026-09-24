using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// The fleet module's change signal (ET-379): every fleet command handler publishes it on the local bus once its write
/// is done, and each host's relay decides who else hears it — the server pushes it to the fleet's audience, a client
/// republishes the server's push on its own bus. Fleet-scoped (the envelope carries the fleet id) and server-sourced
/// (the client receive loop stamps the originating server, since fleet ids are per-server and must be matched together
/// with it).
///
/// <para>Only <see cref="IntegrationEvent{T}.Data"/> travels the wire. The properties below are facts about the change
/// that only the handler knows and a relay needs to pick its audience; they stay on the host that published it.</para>
/// </summary>
public sealed class FleetChangedEvent(FleetChangePayload data, int? characterId = null)
    : IntegrationEvent<FleetChangePayload>(data, characterId), IFleetScopedEvent, IServerSourcedEvent
{
    public override string EventType => "fleet.changed";

    public long FleetId => Data.FleetId;

    public string? SourceServerAddress { get; set; }

    /// <summary>The character whose command made the change. A relay hears it back to them as well (the echo rule),
    /// even when they are not on the roster — a requester, a declining invitee, a member who just left.</summary>
    public int? ActingCharacterId { get; init; }

    /// <summary>Whether the fleet was open for discovery before the change. Set by a change that can take it out of
    /// discovery (an edit, a conclude, a disband), so the non-members whose row has to go are still reached.</summary>
    public bool WasListed { get; init; }

    /// <summary>A character the change took off this fleet's roster, who has to hear it although the roster no longer
    /// holds them.</summary>
    public int? FormerMemberCharacterId { get; init; }

    /// <summary>The roster and owner of a fleet the change deleted outright (ET-383), who have to hear it although no
    /// fleet is left to read them from.</summary>
    public IReadOnlyList<int> FormerRosterCharacterIds { get; init; } = [];
}
