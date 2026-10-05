using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using EveUtils.Shared.Logging;

namespace EveUtils.Client.Opsec;

/// <summary>
/// Location text (ET-417) travels through view models wrapped in an invisible marker pair, so the one renderer at the
/// text edge can mask it wherever it ends up: alone in a cell just as well as inside "site · system · date". A view
/// model only marks; whether it is masked is decided when it is drawn, so turning OPSEC on or off redraws without
/// rebuilding a single string.
/// </summary>
/// <remarks>The markers are the Unicode interlinear annotation anchor and terminator: format characters EVE never
/// puts in a name. They never leave the screen: the clipboard and the log files strip them, and nothing marked is
/// handed back to the domain, the store or a server.</remarks>
public static class OpsecText
{
    public const char Open = DisplayMarkers.Open;
    public const char Close = DisplayMarkers.Close;

    [return: NotNullIfNotNull(nameof(location))]
    public static string? Mark(string? location) =>
        string.IsNullOrEmpty(location) ? location : $"{Open}{location}{Close}";

    public static bool IsMarked([NotNullWhen(true)] string? text) => text is not null && text.Contains(Open);

    /// <summary>The text with its markers removed and every location left readable.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Strip(string? text) => Replace(text, location => location);

    /// <summary>The text with every marked location replaced by <paramref name="render"/>'s answer for it.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Replace(string? text, Func<string, string> render)
    {
        if (!IsMarked(text))
            return text;

        var rendered = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf(Open, position);
            if (open < 0)
            {
                rendered.Append(text, position, text.Length - position);
                break;
            }

            rendered.Append(text, position, open - position);

            // An unterminated marker (a marked string cut short somewhere) counts to the end: better a masked tail
            // than a readable one.
            var close = text.IndexOf(Close, open + 1);
            var end = close < 0 ? text.Length : close;
            rendered.Append(render(text.Substring(open + 1, end - open - 1)));
            position = close < 0 ? text.Length : close + 1;
        }

        return rendered.ToString();
    }
}
