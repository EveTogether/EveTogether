using System.Globalization;

namespace EveUtils.Server.DataExplorer;

/// <summary>The exact moments the panes print beside a relative time, always in UTC so every admin reads the same.</summary>
public static class UtcText
{
    public static string Minutes(DateTimeOffset at) => Minutes(at.UtcDateTime);

    public static string Minutes(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Run columns are UTC by name but come back from SQLite unspecified; this pins them before comparing.</summary>
    public static DateTimeOffset AsOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
