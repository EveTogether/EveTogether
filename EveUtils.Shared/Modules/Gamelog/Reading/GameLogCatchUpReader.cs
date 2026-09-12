using System.Text;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// A one-off, bounded read of one character's gamelog for a window of time already in the past (ET-258) — beside
/// <see cref="GameLogWatcher"/>'s live tail, never through it: <see cref="GameLogWatcher.Start"/> baselines every
/// file to its current length, so a run resumed after the app was closed would otherwise never see what EVE wrote
/// while nothing was watching.
///
/// EVE starts a new gamelog file per client session, so a crash spanning a restart can leave the character's lines
/// split across more than one file — every file is checked, not just the newest.
/// </summary>
public static class GameLogCatchUpReader
{
    // Same filename shape GameLogWatcher tracks (its own copy is private to that class).
    private static readonly Regex CharacterLogName = new(@"^\d{8}_\d{6}_\d+\.txt$", RegexOptions.Compiled);

    /// <summary>
    /// Every event this character's gamelog carries strictly after <paramref name="sinceUtc"/> and up to
    /// <paramref name="untilUtc"/>, oldest first. <paramref name="sinceUtc"/> is the floor a caller already knows
    /// was processed (ET-254's <c>Run.LastAliveAtUtc</c>) — exclusive, so nothing at or before it is read twice.
    ///
    /// Gamelog lines carry EVE's own wall-clock time, which is UTC with no offset of its own
    /// (<see cref="LogLineParser.Parse"/> reads it as <see cref="DateTimeKind.Unspecified"/>). Both bounds must be
    /// UTC values compared by value, never through <c>ToUniversalTime()</c>/<c>ToLocalTime()</c>, which would
    /// misread an unspecified-kind value as local time (the exact mistake ET-244 found and fixed elsewhere).
    /// </summary>
    public static IReadOnlyList<GameLogEvent> Read(string directory, string characterName, DateTime sinceUtc, DateTime untilUtc) =>
        Read(directory, [characterName], sinceUtc, untilUtc).GetValueOrDefault(characterName) ?? [];

    /// <summary>The same read for several characters at once, in one pass over the directory — keyed by the name as
    /// asked, case-insensitively. A name nobody's gamelog carries is simply absent.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<GameLogEvent>> Read(
        string directory, IReadOnlyCollection<string> characterNames, DateTime sinceUtc, DateTime untilUtc)
    {
        Dictionary<string, List<GameLogEvent>> events = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> wanted = new(characterNames, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new Dictionary<string, IReadOnlyList<GameLogEvent>>();

        foreach (string path in Directory.EnumerateFiles(directory, "*.txt"))
        {
            if (!CharacterLogName.IsMatch(Path.GetFileName(path)))
                continue;

            string text;
            GameLogHeader? header;
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // The header first: most of a gamelog folder belongs to other characters or other days, and reading
                // those to the end only to throw them away is what made a read over a whole folder slow.
                header = GameLogHeader.TryRead(stream);
                if (header is null || !wanted.Contains(header.CharacterName))
                    continue;

                stream.Position = 0;
                using StreamReader reader = new(stream, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }

            if (!events.TryGetValue(header.CharacterName, out List<GameLogEvent>? own))
                events[header.CharacterName] = own = [];
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0)
                    continue;

                if (LogLineParser.Parse(trimmed) is not { } parsed)
                    continue;

                if (parsed.Timestamp > sinceUtc && parsed.Timestamp <= untilUtc)
                    own.Add(parsed);
            }
        }

        return events.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<GameLogEvent>)[.. pair.Value.OrderBy(e => e.Timestamp)],
            StringComparer.OrdinalIgnoreCase);
    }
}
