namespace EveUtils.Client.Fleet;

/// <summary>
/// One fleet a character is a member of, independent of whether it has been started. Broader than
/// <see cref="FleetParticipant"/>, which only holds fleets passing
/// <see cref="FleetParticipationRefresher.Participates"/>: <see cref="FleetParticipant"/> answers "where do I
/// broadcast", this answers "where am I a member". Kept as its own list on <see cref="IFleetParticipation"/> rather
/// than folded into <see cref="IFleetParticipation.Current"/>, so that widening what a run's notice can see never
/// also widens what the metric publisher shares for (ET-29, next to ET-165).
/// </summary>
public readonly record struct FleetMembership(int CharacterId, long FleetId, string Name, bool ClientOnly);
