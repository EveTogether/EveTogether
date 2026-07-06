namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// An implant plugged into the character in a calculation request: just its type. Implants are passive, char-anchored
/// items — their bonuses (to the ship, or to modules requiring a skill) flow through the normal effect routing, so they
/// only need to be present in the graph as a source.
/// </summary>
public sealed record ImplantInput(int TypeId);
