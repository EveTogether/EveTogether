namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's own share of an activity's loot — one row per participant whose own run has priced captures,
/// named the same way <see cref="ActivityBountyRowViewModel"/> and <see cref="ActivityEnemyCharacterRowViewModel"/>
/// already break bounty and enemies down (ET-211: loot copied from a character's own client now lands on that
/// character's own run, so it can finally be attributed here too, the same way theirs always could).
/// </summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — see
/// <see cref="ActivityRunRowViewModel"/> for why this is not always possible yet.</param>
public sealed class ActivityLootCharacterRowViewModel(long characterId, decimal netIsk, Func<long, string>? nameOf = null)
{
    public string CharacterText { get; } = nameOf?.Invoke(characterId) ?? $"character {characterId}";

    public string IskText { get; } = $"{netIsk:N2} ISK";
}
