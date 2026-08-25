using System.Globalization;

namespace EveUtils.Client.Updates;

/// <summary>
/// Renders a download size the way the offer shows it. Invariant on purpose: the rest of the update UI is English,
/// so a Dutch machine must not turn "78 MB" into "78,4 MB" beside it.
/// </summary>
public static class UpdateDownloadSize
{
    private const long Megabyte = 1024 * 1024;

    /// <summary>
    /// Formats <paramref name="bytes"/> as whole megabytes, floored to at least 1 MB for anything non-empty.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
            return "unknown";

        var megabytes = bytes < Megabyte ? 1 : bytes / Megabyte;

        return string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");
    }
}
