namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A roster member's self-reported availability for a fleet that has not started yet (ET-169). Distinct from
/// leaving the roster: a signed-off member stays listed, so the commander sees both that they were asked and
/// that they should not be counted on. Set and cleared by the member only — never by the commander.
/// <see cref="NotSet"/> counts as available (silence is not a no); <see cref="SignedOff"/> is the one state that
/// keeps a member off the start count and out of the collision tally. Every member resets to <see cref="NotSet"/>
/// when the fleet starts — a sign-off covers the next start, not every future one.
/// </summary>
public enum FleetMemberAvailability
{
    NotSet = 0,
    Available = 1,
    SignedOff = 2
}
