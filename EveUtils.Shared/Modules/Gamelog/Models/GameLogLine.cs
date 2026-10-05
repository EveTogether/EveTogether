namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>One line of a character's game log as it is shown: the time EVE wrote it (UTC, unspecified kind — see
/// <c>GameLogCatchUpReader</c>), its kind and the text without the game's markup.</summary>
public sealed record GameLogLine(string Character, DateTime Timestamp, GameLogLineKind Kind, string Text);
