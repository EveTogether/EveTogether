using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Queries;

/// <summary>
/// Which of a fleet's roster members are, right now, counting for a <i>different</i> started fleet — the collision
/// the fleet commander has to see before pressing START (ET-168, scherm 2). Read-only and free of side effects: it
/// answers the same question <c>StartFleetCommandHandler</c> answers again while starting, but early enough that the
/// dialog can name it.
///
/// Only this server can answer it. A client sees the fleets it is itself in, so it can work out where its own pilots
/// count; it cannot see that someone else's pilot is flying in a fleet of theirs. That is exactly the half the
/// commander needs, and the reason this is a round trip rather than a local sum.
/// </summary>
public sealed record ListMembersActiveElsewhereQuery(long FleetId)
    : IQuery<IReadOnlyList<FleetMemberElsewhereInfo>>;
