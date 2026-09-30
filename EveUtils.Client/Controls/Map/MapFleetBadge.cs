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

    /// <summary>The hover text: the system, then the commander as "FC", then each other member with the age of their
    /// position, freshest first.</summary>
    public string Describe(string systemHeading, DateTimeOffset now) =>
        string.Join('\n', Members
            .OrderByDescending(member => member.IsCommander)
            .ThenBy(member => now - member.ObservedAt)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .Select(member => $"{(member.IsCommander ? "FC · " : string.Empty)}{member.Name} · {Ago(now - member.ObservedAt)}")
            .Prepend(systemHeading));

    public static string Ago(TimeSpan age)
    {
        int seconds = (int)Math.Max(0, Math.Round(age.TotalSeconds));
        return seconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds}s ago")
            : seconds < 3600 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min ago")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h ago");
    }
}
