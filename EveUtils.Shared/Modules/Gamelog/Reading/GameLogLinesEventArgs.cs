namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>The complete lines one poll read from a file. <see cref="EndOffset"/> is where that read stopped, which is
/// how a reader that also read the file's history up to an earlier offset tells what it already has.</summary>
public sealed class GameLogLinesEventArgs(string path, string characterName, long endOffset, IReadOnlyList<string> lines) : EventArgs
{
    public string Path { get; } = path;
    public string CharacterName { get; } = characterName;
    public long EndOffset { get; } = endOffset;
    public IReadOnlyList<string> Lines { get; } = lines;
}
