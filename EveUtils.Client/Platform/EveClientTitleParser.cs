using System;
using System.Text.RegularExpressions;

namespace EveUtils.Client.Platform;

/// <summary>
/// The shared, pure parsing for EVE client evidence, separated from the per-OS process plumbing so the
/// fragile parts are unit-testable on any platform.
/// </summary>
public static partial class EveClientTitleParser
{
    /// <summary>A logged-in EVE client titles its window "EVE - &lt;CharacterName&gt;"; the character-selection
    /// screen is just "EVE", so it never matches. The name is everything after the prefix — NOT a '-'-split,
    /// which would truncate character names that contain a dash themselves.</summary>
    public const string WindowTitlePrefix = "EVE - ";

    [GeneratedRegex(@"/autoSelectCharacter:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AutoSelectCharacter();

    /// <summary>The character name from a client window title, or null when the title is not a logged-in EVE client.</summary>
    public static string? CharacterNameFromTitle(string? title)
    {
        if (title is null || !title.StartsWith(WindowTitlePrefix, StringComparison.Ordinal))
            return null;
        var name = title[WindowTitlePrefix.Length..].Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>The character id from an EVE client command line (<c>exefile.exe … /autoSelectCharacter:&lt;id&gt;</c>),
    /// or null when the command line is not an EVE client or carries no selection.</summary>
    public static int? CharacterIdFromCommandLine(string? commandLine)
    {
        if (commandLine is null || !commandLine.Contains("exefile.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = AutoSelectCharacter().Match(commandLine);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>The character name from one <c>wmctrl -l</c> line ("&lt;id&gt; &lt;desktop&gt; &lt;host&gt; &lt;title&gt;"),
    /// or null when the line is not a logged-in EVE client window.</summary>
    public static string? CharacterNameFromWmctrlLine(string line)
    {
        var parts = line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 4 ? null : CharacterNameFromTitle(parts[3].Trim());
    }
}
