namespace EveUtils.Shared.Logging;

/// <summary>
/// The pair of format characters the client wraps location text in for its screens (ET-417, <c>OpsecText</c>), so the
/// screen can mask it. Defined here because a log line can carry them too, and a log file is not a screen: what is
/// written to disk carries the plain text.
/// </summary>
public static class DisplayMarkers
{
    public const char Open = '￹';
    public const char Close = '￻';

    public static string Strip(string text) =>
        text.Contains(Open) || text.Contains(Close)
            ? text.Replace(Open.ToString(), string.Empty).Replace(Close.ToString(), string.Empty)
            : text;
}
