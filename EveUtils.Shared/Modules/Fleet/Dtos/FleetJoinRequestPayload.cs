namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// Result of a request-to-join, returned by the create handler. Carries the fleet name so the
/// requester can show "requested to join &lt;fleet&gt;" without a second lookup.
/// </summary>
public sealed record FleetJoinRequestPayload(
    long RequestId,
    long FleetId,
    string FleetName,
    int RequesterCharacterId);
