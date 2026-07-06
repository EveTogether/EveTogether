using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EveUtils.Shared.Modules.Gamelog.Reading;

/// <summary>Reads the gamelog header (character + session start). Folded from the EVE-Utils demo.</summary>
public sealed record GameLogHeader(string CharacterName, DateTime SessionStarted)
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";
    private const int MaxHeaderLines = 10;

    private static readonly Regex ListenerRegex = new(@"Listener:\s*(?<name>.+)", RegexOptions.Compiled);
    private static readonly Regex SessionRegex = new(@"Session Started:\s*(?<ts>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    /// <summary>
    /// Reads the gamelog header. Returns null for header-only / character-select files (no Listener
    /// line), which should not be tracked.
    /// </summary>
    public static GameLogHeader? TryRead(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        string? characterName = null;
        DateTime? sessionStarted = null;

        for (var i = 0; i < MaxHeaderLines; i++)
        {
            var line = reader.ReadLine();
            if (line is null)
                break;

            var listener = ListenerRegex.Match(line);
            if (listener.Success)
                characterName = listener.Groups["name"].Value.Trim();

            var session = SessionRegex.Match(line);
            if (session.Success &&
                DateTime.TryParseExact(session.Groups["ts"].Value, TimestampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
                sessionStarted = started;

            if (characterName is not null && sessionStarted is not null)
                break;
        }

        return characterName is not null && sessionStarted is not null
            ? new GameLogHeader(characterName, sessionStarted.Value)
            : null;
    }
}
