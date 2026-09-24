using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Gamelog.Languages;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>
/// Reads the gamelog header (character + session start) and, from the words in it, the client language the file is
/// written in. Folded from the EVE-Utils demo.
/// </summary>
public sealed record GameLogHeader(string CharacterName, DateTime SessionStarted, GamelogLanguage Language = GamelogLanguage.English)
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";
    private const string TimestampPattern = @"\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}";
    private const int MaxHeaderLines = 10;

    // "<word>: <name>" and "<word>: <timestamp>" for each language whose words are known; the colon is followed by
    // optional spacing on both sides because some languages set a space before it.
    private static readonly IReadOnlyList<(GamelogLanguage Language, Regex Listener, Regex SessionStarted)> KnownLanguages =
    [
        .. GamelogTemplateTables.All.Select(pair => (
            pair.Key,
            new Regex($@"{Regex.Escape(pair.Value.Listener)}\s*[:：]\s*(?<name>.+)", RegexOptions.CultureInvariant),
            new Regex($@"{Regex.Escape(pair.Value.SessionStarted)}\s*[:：]\s*(?<ts>{TimestampPattern})", RegexOptions.CultureInvariant)))
    ];

    // The same two lines in a language this build has no words for: a name after a colon, then a timestamp after a colon.
    private static readonly Regex UnknownListener = new(@"^\s*[^\s:：\d][^:：]*[:：]\s*(?<name>[^\d\s].*)$", RegexOptions.CultureInvariant);
    private static readonly Regex UnknownSessionStarted = new($@"^\s*[^\s:：\d][^:：]*[:：]\s*(?<ts>{TimestampPattern})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the gamelog header. Returns null for header-only / character-select files (no Listener
    /// line), which should not be tracked. A header in a language this build has no words for still reads, as
    /// <see cref="GamelogLanguage.Unknown"/>, so the file can be reported rather than silently ignored.
    /// </summary>
    public static GameLogHeader? TryRead(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        List<string> lines = [];
        for (int i = 0; i < MaxHeaderLines && reader.ReadLine() is { } line; i++)
        {
            lines.Add(line);
        }

        foreach ((GamelogLanguage language, Regex listener, Regex sessionStarted) in KnownLanguages)
        {
            if (_Read(lines, listener, sessionStarted) is { } header)
            {
                return header with { Language = language };
            }
        }

        return _Read(lines, UnknownListener, UnknownSessionStarted) is { } unknown
            ? unknown with { Language = GamelogLanguage.Unknown }
            : null;
    }

    private static GameLogHeader? _Read(IReadOnlyList<string> lines, Regex listener, Regex sessionStarted)
    {
        string? characterName = null;
        DateTime? started = null;

        foreach (string line in lines)
        {
            if (characterName is null && listener.Match(line) is { Success: true } named)
            {
                characterName = named.Groups["name"].Value.Trim();
            }

            if (started is null
                && sessionStarted.Match(line) is { Success: true } session
                && DateTime.TryParseExact(session.Groups["ts"].Value, TimestampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                started = parsed;
            }
        }

        return characterName is not null && started is not null ? new GameLogHeader(characterName, started.Value) : null;
    }
}
