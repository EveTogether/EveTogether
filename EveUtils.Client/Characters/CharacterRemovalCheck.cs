namespace EveUtils.Client.Characters;

/// <summary>What stands in the way of removing a character, asked before the confirmation is shown (ET-345).</summary>
/// <param name="BlockingFleetName">An active fleet this character created. Removing it would leave that fleet without
/// its commander, so the player hands it over or stops it first.</param>
/// <param name="OpenRunIds">The character's runs still on the clock; they are stopped before the removal.</param>
public sealed record CharacterRemovalCheck(string? BlockingFleetName, IReadOnlyList<Guid> OpenRunIds)
{
    public bool IsBlocked => BlockingFleetName is not null;

    public bool HasOpenRun => OpenRunIds.Count > 0;
}
