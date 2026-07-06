namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// An environment/weather selected for a calculation: the type id of an "Effect Beacon" (group
/// 920) whose category-7 "system" effects modify the ship's attributes — a wormhole/metaliminal beacon from the SDE, or
/// a synthetic abyssal beacon from the patch layer. The beacon is injected as one extra ship-anchored, always-on source
/// (exactly like an implant), so its <c>shipID</c>-domain modifiers run through the normal resolve. No weather selected
/// (<see cref="FitInput.Weather"/> null) means the source is simply absent — the calculation is byte-identical.
/// </summary>
public sealed record WeatherInput(int TypeId);
