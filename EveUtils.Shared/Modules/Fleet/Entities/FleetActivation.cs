namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// In-game lifecycle phase of a fleet. Distinct from <see cref="FleetState"/>, which is the soft-delete
/// lifecycle: a fleet can be <see cref="FleetState.Active"/> (not archived) yet still <see cref="Forming"/>.
/// A fleet is created <see cref="Forming"/>, the creator flips it to <see cref="Active"/> with the Start action
/// (which notifies the roster and begins broadcasting), then marks it <see cref="Concluded"/> with the
/// Conclude action when the op is over — a finished fleet kept for history. Only an <see cref="Active"/> fleet
/// broadcasts metrics; <see cref="Forming"/> and <see cref="Concluded"/> fleets broadcast nothing, and a
/// <see cref="Concluded"/> fleet can no longer be joined.
/// </summary>
public enum FleetActivation
{
    Forming = 0,
    Active = 1,
    Concluded = 2
}
