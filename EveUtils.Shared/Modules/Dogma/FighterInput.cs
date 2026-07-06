namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A launched fighter squadron in a calculation request: its type and how many fighters in the squadron are active.
/// Fighters are char-owned items (their skill bonuses arrive via <c>OwnerRequiredSkillModifier</c>) and a squadron's DPS
/// scales by <see cref="ActiveCount"/> — the simulated number of fighters firing, 1..squadron size (the bay UI's per-tube
/// "- / +"). Only squadrons in launch tubes are passed; bay reserves deal no damage.
/// </summary>
public sealed record FighterInput(int TypeId, int ActiveCount = 1);
