using System.Text;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// Reads the lines a set of gamelog files already held (ET-410), each only up to the offset the watcher had reached —
/// what lies beyond that arrives through <see cref="GameLogWatcher.LinesRead"/>, so nothing is read twice and the
/// files are never tailed a second time. Read-only: the files are opened with <see cref="FileShare.ReadWrite"/> because
/// EVE is still writing to them.
/// </summary>
public static class GameLogHistoryReader
{
    /// <summary>Every line of the given files whose time falls in [<paramref name="sinceUtc"/>, <paramref name="untilUtc"/>).
    /// Gamelog time is UTC without an offset, so both bounds are compared by value and never converted.</summary>
    public static IReadOnlyList<GameLogLine> Read(IReadOnlyDictionary<string, long> limits, DateTime sinceUtc, DateTime untilUtc)
    {
        List<GameLogLine> lines = [];
        foreach ((string path, long limit) in limits)
        {
            if (limit > 0 && _MayHoldLines(path, sinceUtc))
                _ReadFile(path, limit, sinceUtc, untilUtc, lines);
        }

        return lines;
    }

    // Most of a gamelog folder is other days: a file nobody wrote to since the window opened has nothing in it.
    private static bool _MayHoldLines(string path, DateTime sinceUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) >= DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void _ReadFile(string path, long limit, DateTime sinceUtc, DateTime untilUtc, List<GameLogLine> into)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (GameLogHeader.TryRead(stream) is not { } header)
                return;

            long end = Math.Min(limit, stream.Length);
            if (end == 0)
                return;

            // The watcher holds an unfinished last line back and emits it whole later; reading it here would show it twice.
            stream.Position = end - 1;
            bool endsOnLineBreak = stream.ReadByte() == '\n';

            stream.Position = 0;
            using StreamReader reader = new(new BoundedStream(stream, end), Encoding.UTF8);
            List<string> fileLines = [];
            while (reader.ReadLine() is { } line)
                fileLines.Add(line);

            if (!endsOnLineBreak && fileLines.Count > 0)
                fileLines.RemoveAt(fileLines.Count - 1);

            foreach (string line in fileLines)
            {
                if (LogLineParser.ParseLine(line.TrimEnd('\r'), header.CharacterName) is { } parsed
                    && parsed.Timestamp >= sinceUtc && parsed.Timestamp < untilUtc)
                {
                    into.Add(parsed);
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
