namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The one precedence a character's display name follows wherever a run may have recorded one (ET-212): the
/// snapshot stored on the run at start time, then a live lookup for someone still logged in on this machine, then
/// the bare id. <see cref="ActivityRunRowViewModel"/> and the runs overview's crew line (ET-247) both go through
/// this so the short line and the expanded one can never name the same pilot two different ways.
/// </summary>
internal static class CharacterNameResolver
{
    public static string Resolve(string? nameSnapshot, long characterId, Func<long, string>? nameOf) =>
        !string.IsNullOrEmpty(nameSnapshot)
            ? nameSnapshot
            : nameOf?.Invoke(characterId) ?? $"character {characterId}";
}
