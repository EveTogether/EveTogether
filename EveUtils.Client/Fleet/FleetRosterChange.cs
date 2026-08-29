namespace EveUtils.Client.Fleet;

/// <summary>What happened to a fleet's roster. The kinds a screen actually reacts to differently: a member who is
/// gone can be taken off a list without re-reading anything, where an add or a role/fit change is only known by
/// reading the roster again.</summary>
public enum FleetRosterChangeKind
{
    /// <summary>A pilot was removed from the fleet, or left it. <see cref="FleetRosterChange.CharacterId"/> is theirs.</summary>
    MemberRemoved,

    /// <summary>A pilot was added to the fleet (added, invited-and-accepted, joined).</summary>
    MemberAdded,

    /// <summary>A member is still in the fleet but something a screen shows about them moved — their position, their
    /// role, the fit they fly.</summary>
    MemberChanged,

    /// <summary>The roster changed in a way this client cannot attribute to one pilot — a server-pushed
    /// <c>fleet.changed</c>, a swap, an ownership transfer. Everything showing this fleet re-reads it.</summary>
    RosterReloaded
}

/// <summary>
/// One announcement that a fleet's roster moved, carried by <see cref="IFleetRosterWatch"/> to every screen that
/// shows that roster. <see cref="CharacterId"/> is 0 for <see cref="FleetRosterChangeKind.RosterReloaded"/>, which
/// names no single pilot.
/// </summary>
public sealed record FleetRosterChange(long FleetId, int CharacterId, FleetRosterChangeKind Kind)
{
    public static FleetRosterChange Removed(long fleetId, int characterId) =>
        new(fleetId, characterId, FleetRosterChangeKind.MemberRemoved);

    public static FleetRosterChange Added(long fleetId, int characterId) =>
        new(fleetId, characterId, FleetRosterChangeKind.MemberAdded);

    public static FleetRosterChange Changed(long fleetId, int characterId) =>
        new(fleetId, characterId, FleetRosterChangeKind.MemberChanged);

    public static FleetRosterChange Reloaded(long fleetId) =>
        new(fleetId, 0, FleetRosterChangeKind.RosterReloaded);
}
