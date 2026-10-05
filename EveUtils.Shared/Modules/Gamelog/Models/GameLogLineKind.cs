namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>What a game log line is about, for showing and filtering it. Coarser than <see cref="LogCategory"/>: the game's
/// question and hint lines read alike, and a line with no category of its own is <see cref="Other"/>.</summary>
public enum GameLogLineKind
{
    Other,
    Combat,
    Mining,
    Travel,
    Notify,
    Info,
    Hint,
    Bounty
}
