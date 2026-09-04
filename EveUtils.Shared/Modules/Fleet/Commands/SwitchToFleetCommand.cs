using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// One character moves itself from whatever started fleet it counts for into this one (ET-168, scherm 7). In code
/// this is two acts — leave the other fleet, then couple here — and accepting an invite while still active
/// elsewhere is refused for exactly that reason (<see cref="ActiveFleetMembershipGuard"/>: <i>leave or conclude it
/// before joining another</i>). This command is the seam that makes the two feel like one, so a member answering
/// "yes, I'm coming" does not have to know that the code counts to two.
///
/// <para>No app-permission gate: switching is the character's own act, the way responding to an invite is. The
/// authorization is that <see cref="ActingCharacterId"/> comes from the validated session, so a character can only
/// ever switch itself — which is also why a commander may move their <i>own</i> alt and nobody else's. That is not
/// an FC right; it is owning the character.</para>
///
/// <para>Nothing here removes anyone from a roster they meant to stay on except the one they are leaving: staying
/// put keeps you on this fleet's roster, merely not linked, and the door back stays open for as long as the fleet
/// runs.</para>
///
/// <para>Returns the fleets that were left, because a switch changes two rosters and the fleet left behind has
/// watchers of its own who should not have to find out by refreshing.</para>
/// </summary>
public sealed record SwitchToFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result<IReadOnlyList<long>>>;
