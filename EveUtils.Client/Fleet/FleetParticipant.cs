namespace EveUtils.Client.Fleet;

/// <summary>
/// One (character, fleet) pair the client currently publishes metrics for: a fleet the character is a member of on a
/// connected server (<see cref="ClientOnly"/> = false → routed Both), or a client-only fleet
/// (<see cref="ClientOnly"/> = true → routed Local, never over gRPC).
/// </summary>
/// <param name="FleetCommanderCharacterId">Who holds
/// <see cref="Shared.Modules.Fleet.Entities.FleetRole.FleetCommander"/> in that fleet, or null when the roster could
/// not be read. Taken from the ET roster, which is where a human actually appoints an FC — not from the ESI fleet
/// boss, which only answers for a fleet coupled to an in-game one and so left every uncoupled fleet's commander
/// without run controls (ET-152).</param>
/// <param name="ServerAddress">The server the fleet lives on, null for a client-only fleet. A fleet id is only unique
/// per server, so this is what lets a fleet run be published to its own fleet's server rather than to a guess
/// (ET-245).</param>
public readonly record struct FleetParticipant(
    int CharacterId, long FleetId, bool ClientOnly, int? FleetCommanderCharacterId = null, string? ServerAddress = null);
