using System.Linq;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>Builds the member list <see cref="FleetCommanderPresence.From"/> counts, for tests about the ratio rather
/// than about presence.</summary>
internal static class FleetStandings
{
    /// <summary>Members who are here, at these systems — null for one who shares no position. Nobody is offline, so a
    /// test that is about the ratio does not have to say so member by member.</summary>
    public static FleetMemberStanding[] At(params string?[] systems) =>
        [.. systems.Select(system => new FleetMemberStanding(system, IsOffline: false))];

    /// <summary>A member who has gone. Offline has no location by construction — that is the ET-71 invariant the
    /// badge is counted on — so there is nothing to pass.</summary>
    public static FleetMemberStanding Gone => new(null, IsOffline: true);
}
