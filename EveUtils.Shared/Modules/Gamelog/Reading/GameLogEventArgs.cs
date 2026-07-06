using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

public sealed class GameLogEventArgs(string characterName, GameLogEvent logEvent) : EventArgs
{
    public string CharacterName { get; } = characterName;
    public GameLogEvent LogEvent { get; } = logEvent;
}
