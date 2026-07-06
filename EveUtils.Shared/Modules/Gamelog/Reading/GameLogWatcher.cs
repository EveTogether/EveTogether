using System.Text;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// Tails EVE gamelog files in a directory. Only files that appear or grow while the watcher runs are
/// processed (i.e. characters that are currently online). History present before <see cref="Start"/>
/// is skipped. Folded from the EVE-Utils demo (own code).
/// </summary>
public sealed partial class GameLogWatcher : IDisposable
{
    [GeneratedRegex(@"^\d{8}_\d{6}_\d+\.txt$")]
    private static partial Regex CharacterLogName();

    private const int MaxHeaderScan = 80;

    private readonly string _directory;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, TrackedFile> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private Timer? _timer;
    private bool _started;
    private int _polling;

    public GameLogWatcher(string directory, TimeSpan? pollInterval = null)
    {
        _directory = directory;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public event EventHandler<GameLogEventArgs>? EventParsed;
    public event EventHandler<string>? CharacterDetected;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
        }

        // Baseline existing files at their current length so we don't replay history.
        if (Directory.Exists(_directory))
            foreach (var path in Directory.EnumerateFiles(_directory, "*.txt"))
                if (CharacterLogName().IsMatch(Path.GetFileName(path)))
                    Baseline(path);

        _timer = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
    }

    private void Baseline(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            lock (_gate)
                _tracked[path] = new TrackedFile { Offset = length };
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Surface characters that are online but whose log hasn't produced new lines yet. Finds each
    /// name's most recent log by scanning only the newest file headers, then emits the character +
    /// last known location without replaying historical combat/mining.
    /// </summary>
    public void TrackByCharacterNames(IReadOnlyCollection<string> characterNames)
    {
        List<string> remaining;
        lock (_gate)
            remaining = characterNames.Where(n => !_known.Contains(n)).ToList();

        if (remaining.Count == 0 || !Directory.Exists(_directory))
            return;

        var newestFirst = Directory.EnumerateFiles(_directory, "*.txt")
            .Where(p => CharacterLogName().IsMatch(Path.GetFileName(p)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Take(MaxHeaderScan);

        foreach (var path in newestFirst)
        {
            if (remaining.Count == 0)
                break;

            GameLogHeader? header;
            long length;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                header = GameLogHeader.TryRead(stream);
                length = stream.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (header is null)
                continue;

            var match = remaining.FirstOrDefault(n => string.Equals(n, header.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;

            remaining.Remove(match);
            lock (_gate)
            {
                if (!_tracked.TryGetValue(path, out var tracked))
                {
                    tracked = new TrackedFile();
                    _tracked[path] = tracked;
                }
                tracked.Offset = length; // start tailing from the end; don't replay history
                tracked.Header = header;
                _known.Add(header.CharacterName);
            }

            CharacterDetected?.Invoke(this, header.CharacterName);
            EmitLastKnownLocation(path, header.CharacterName);
        }
    }

    private void Poll()
    {
        // Skip this tick if the previous poll is still running — Timer callbacks can overlap, and two polls reading
        // the same TrackedFile would emit duplicate events and fight over its offset.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            if (!Directory.Exists(_directory))
                return;

            foreach (var path in Directory.EnumerateFiles(_directory, "*.txt"))
            {
                if (!CharacterLogName().IsMatch(Path.GetFileName(path)))
                    continue;

                // Atomic get-or-create: a new file created while running starts at offset 0. Doing the lookup and the
                // insert in one lock scope stops a concurrent TrackByCharacterNames entry (offset = end) from being
                // clobbered with offset 0, which would replay the whole file's history.
                TrackedFile tracked;
                lock (_gate)
                {
                    if (!_tracked.TryGetValue(path, out var existing))
                    {
                        existing = new TrackedFile { Offset = 0 };
                        _tracked[path] = existing;
                    }
                    tracked = existing;
                }

                ReadNew(path, tracked);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void ReadNew(string path, TrackedFile tracked)
    {
        try
        {
            // Snapshot the tracked state under the gate; the file I/O and event dispatch below run without holding it
            // (so a handler can't deadlock on the gate), then the new state is written back atomically.
            long offset;
            GameLogHeader? header;
            string leftover;
            lock (_gate)
            {
                offset = tracked.Offset;
                header = tracked.Header;
                leftover = tracked.Leftover;
            }

            var length = new FileInfo(path).Length;
            if (length <= offset)
                return;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var headerJustDetected = false;
            if (header is null)
            {
                header = GameLogHeader.TryRead(stream);
                headerJustDetected = header is not null;
            }

            if (header is null)
            {
                lock (_gate)
                    tracked.Offset = length;
                return;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var combined = leftover + text;
            var lastBreak = combined.LastIndexOf('\n');
            var completeBlock = lastBreak < 0 ? string.Empty : combined[..lastBreak];
            var newLeftover = lastBreak < 0 ? combined : combined[(lastBreak + 1)..];

            lock (_gate)
            {
                tracked.Header = header;
                tracked.Offset = length;
                tracked.Leftover = newLeftover;
                if (headerJustDetected)
                    _known.Add(header.CharacterName);
            }

            if (headerJustDetected)
            {
                CharacterDetected?.Invoke(this, header.CharacterName);

                // For files that already existed when watching started, surface the last known location from history
                // so the system is known immediately (without replaying historical combat/mining).
                if (offset > 0)
                    EmitLastKnownLocation(path, header.CharacterName);
            }

            if (completeBlock.Length == 0)
                return;

            foreach (var line in completeBlock.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0)
                    continue;

                var parsed = LogLineParser.Parse(trimmed);
                if (parsed is not null)
                    EventParsed?.Invoke(this, new GameLogEventArgs(header.CharacterName, parsed));
            }
        }
        catch (IOException)
        {
        }
    }

    private void EmitLastKnownLocation(string path, string characterName)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("Jumping from", StringComparison.Ordinal) &&
                    !line.Contains("Undocking from", StringComparison.Ordinal))
                    continue;

                if (LogLineParser.Parse(line.TrimEnd('\r')) is LocationEvent location)
                {
                    EventParsed?.Invoke(this, new GameLogEventArgs(characterName, location));
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private sealed class TrackedFile
    {
        public long Offset { get; set; }
        public string Leftover { get; set; } = string.Empty;
        public GameLogHeader? Header { get; set; }
    }
}
