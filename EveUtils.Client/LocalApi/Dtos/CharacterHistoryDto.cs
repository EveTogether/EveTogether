namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// The recent combat of one running character, sent once in the <c>snapshot</c> so a widget that connects mid-fight can
/// draw its graph straight away. Every array holds one value per <c>IntervalSeconds</c>, oldest first with the newest
/// last, all the same length (shorter than the requested span when the character has not been drawn that long). The
/// values are the smoothed ones the DPS pop-out draws: DPS and reps in hp/s, neut and cap in GJ/s.
/// </summary>
public sealed record CharacterHistoryDto(
    int? CharacterId,
    string CharacterName,
    int IntervalSeconds,
    double[] DpsOut,
    double[] DpsIn,
    double[] RepIn,
    double[] RepOut,
    double[] NeutIn,
    double[] NeutOut,
    double[] CapIn,
    double[] CapOut);
