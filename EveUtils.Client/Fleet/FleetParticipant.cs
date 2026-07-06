namespace EveUtils.Client.Fleet;

/// <summary>
/// One (character, fleet) pair the client currently publishes metrics for: a fleet the character is a member of on a
/// connected server (<see cref="ClientOnly"/> = false → routed Both), or a client-only fleet
/// (<see cref="ClientOnly"/> = true → routed Local, never over gRPC).
/// </summary>
public readonly record struct FleetParticipant(int CharacterId, long FleetId, bool ClientOnly);
