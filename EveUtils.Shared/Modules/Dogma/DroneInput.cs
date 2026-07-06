namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A drone group in a calculation request: its type and how many are in space. Drones are char-owned items — their
/// skill bonuses arrive via <c>OwnerRequiredSkillModifier</c> — and their DPS is multiplied by <see cref="Amount"/>.
/// </summary>
public sealed record DroneInput(int TypeId, int Amount = 1);
