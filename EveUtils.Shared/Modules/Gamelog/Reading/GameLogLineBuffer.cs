using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// The game log lines of every character as they are shown (ET-410), fed by the one <see cref="GameLogWatcher"/> that
/// already tails the folder: the lines it has not read yet arrive through <see cref="GameLogWatcher.LinesRead"/>, the
/// lines the files held before through <see cref="GameLogHistoryReader"/>, cut at the offsets the watcher had when this
/// buffer started listening. Memory only — nothing is written anywhere, and the live part is bounded.
/// </summary>
public sealed class GameLogLineBuffer : IDisposable
{
    public const int MaxLiveLines = 100_000;

    private readonly GameLogWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly List<GameLogLine> _live = [];
    private readonly IReadOnlyDictionary<string, long> _historyLimits;

    public GameLogLineBuffer(GameLogWatcher watcher)
    {
        _watcher = watcher;
        // Listening first, then the offsets: a batch read in between is covered by both, and dropped below because its
        // end offset is not past the snapshot.
        _watcher.LinesRead += _OnLinesRead;
        _historyLimits = _watcher.SnapshotOffsets();
    }

    /// <summary>Raised on the watcher's thread with the lines of each batch that is not in the history.</summary>
    public event Action<IReadOnlyList<GameLogLine>>? LinesAdded;

    /// <summary>Every line in [<paramref name="sinceUtc"/>, <paramref name="untilUtc"/>) of every character, oldest first.</summary>
    public IReadOnlyList<GameLogLine> Load(DateTime sinceUtc, DateTime untilUtc)
    {
        List<GameLogLine> lines = [.. GameLogHistoryReader.Read(_historyLimits, sinceUtc, untilUtc)];
        lock (_gate)
            lines.AddRange(_live.Where(line => line.Timestamp >= sinceUtc && line.Timestamp < untilUtc));

        return [.. lines.OrderBy(line => line.Timestamp)];
    }

    public void Dispose() => _watcher.LinesRead -= _OnLinesRead;

    private void _OnLinesRead(object? sender, GameLogLinesEventArgs batch)
    {
        if (_historyLimits.TryGetValue(batch.Path, out long covered) && batch.EndOffset <= covered)
            return;

        List<GameLogLine> added = [.. batch.Lines.Select(line => LogLineParser.ParseLine(line, batch.CharacterName)).OfType<GameLogLine>()];
        if (added.Count == 0)
            return;

        lock (_gate)
        {
            _live.AddRange(added);
            if (_live.Count > MaxLiveLines)
                _live.RemoveRange(0, _live.Count - MaxLiveLines * 9 / 10);
        }

        LinesAdded?.Invoke(added);
    }
}
