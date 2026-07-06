using System.Collections.Generic;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Client-side mirror of the server's active participation: which fleet this client is currently sharing
/// its live metrics with, and as which character(s). Set after a successful <c>EnterFleet</c> and cleared on
/// <c>LeaveFleet</c> (the gRPC round-trip is what the server's <c>ActiveFleetRegistry</c> reroutes on). The
/// <see cref="Fleet.FleetMetricPublisher"/> reads it each tick to decide whether — and for which characters — to
/// publish samples.
///
/// One active fleet at a time, but <b>several local characters</b> can be active in it at once: a user
/// running multiple clients/toons in the same fleet adds each via <see cref="Enter"/>, and the publisher batches
/// every active character's per-character samples into one coordinated 1 Hz flush instead of N uncoordinated
/// streams. Entering a <i>different</i> fleet replaces the previous fleet and its whole active set.
/// </summary>
public interface IActiveFleetState
{
    /// <summary>The fleet this client shares metrics with, or null when not participating.</summary>
    long? ActiveFleetId { get; }

    /// <summary>The most recently entered participating character (stamped onto its <c>MetricSample</c>), or null.</summary>
    int? CharacterId { get; }

    /// <summary>Every local character currently active in <see cref="ActiveFleetId"/> — empty when not participating.</summary>
    IReadOnlyCollection<int> ActiveCharacterIds { get; }

    /// <summary>The server the active fleet lives on — the target for LEAVE and the inline metrics panel when
    /// several servers are coupled. Null when not participating or when the active fleet is client-only.</summary>
    string? ActiveServerAddress { get; }

    /// <summary>
    /// True while the active fleet is a client-only fleet: the publisher then keeps its samples
    /// <c>EventTarget.Local</c> (local graphs only, no gRPC push). False for a normal server-backed fleet and
    /// when not participating.
    /// </summary>
    bool IsActiveFleetClientOnly { get; }

    /// <summary>Add a local character to the active set of <paramref name="fleetId"/>. Entering a
    /// different fleet than the current one replaces the fleet and clears the previous active set.
    /// <paramref name="clientOnly"/> marks the fleet as a client-only fleet so its metrics stay local.</summary>
    void Enter(long fleetId, int characterId, string? serverAddress = null, bool clientOnly = false);

    /// <summary>Stop participating entirely; clears the fleet and every active character.</summary>
    void Leave();

    /// <summary>Remove one local character from the active set; the last one leaving clears the fleet.</summary>
    void Leave(int characterId);
}
