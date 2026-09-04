namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// What a character in the run window's column is asking of the pilot right now. One value rather than a pair of
/// booleans: red and amber are the same slot seen at two distances, and a row that carried both would be two
/// signals at once.
/// </summary>
public enum RunCharacterAttention
{
    /// <summary>Nothing to answer — the resting state, and the one most rows are in.</summary>
    None,

    /// <summary>Amber: worth looking at before it becomes urgent.</summary>
    Warning,

    /// <summary>Red: the clock is running out.</summary>
    Critical
}
