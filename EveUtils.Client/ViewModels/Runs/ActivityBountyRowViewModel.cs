namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's own share of an activity's bounty — one row per participant who logged a payout, named the same
/// way <see cref="ActivityRunRowViewModel"/> names the crew. Built from <c>RunBountyEntryDto</c> grouped by the
/// run's character (ET-210 review finding, 2026-09-09): a saved multi-character activity used to show one combined
/// figure with no way to tell who brought in what, even though the underlying rows always carried it per run.
/// </summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — see
/// <see cref="ActivityRunRowViewModel"/> for why this is not always possible yet.</param>
public sealed class ActivityBountyRowViewModel(long characterId, decimal isk, Func<long, string>? nameOf = null)
{
    public string CharacterText { get; } = nameOf?.Invoke(characterId) ?? $"character {characterId}";

    public string IskText { get; } = $"{isk:N2} ISK";
}
