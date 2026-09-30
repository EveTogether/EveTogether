using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.Controls.Map;

/// <summary>Fleet members in one system: a numbered badge beside it, and who they are on hover.</summary>
/// <param name="SystemIndex">Index into the graph's systems.</param>
public sealed record MapFleetBadge(int SystemIndex, IReadOnlyList<MapFleetSighting> Members)
{
    /// <summary>The fleet commander among <see cref="Members"/>, or null when they are elsewhere or unplaced.</summary>
    public MapFleetSighting? Commander => Members.FirstOrDefault(member => member.IsCommander);

    /// <summary>"12s ago", "3 min ago", "2 h ago".</summary>
    public static string Ago(TimeSpan age)
    {
        int seconds = (int)Math.Max(0, Math.Round(age.TotalSeconds));
        return seconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds}s ago")
            : seconds < 3600 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min ago")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h ago");
    }
}
