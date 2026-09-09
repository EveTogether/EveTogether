namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's own share of an activity's kills — one row per participant who has at least one counted
/// sighting, named the same way <see cref="ActivityRunRowViewModel"/> names the crew. Summed across every enemy
/// type that character logged: the type-by-type detail stays in <see cref="ActivityEnemyRowViewModel"/> below it,
/// this row only answers "how many did THIS character bring down" (ET-210 review finding, 2026-09-09, round 4:
/// Jithran chose per-character tracking with a group total, the same shape <see cref="ActivityBountyRowViewModel"/>
/// already gives bounty).
/// </summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — see
/// <see cref="ActivityRunRowViewModel"/> for why this is not always possible yet.</param>
public sealed class ActivityEnemyCharacterRowViewModel(long characterId, int count, Func<long, string>? nameOf = null)
{
    public string CharacterText { get; } = nameOf?.Invoke(characterId) ?? $"character {characterId}";

    public string CountText { get; } = count == 1 ? "1 enemy" : $"{count} enemies";
}
