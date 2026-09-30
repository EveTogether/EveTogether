using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.Controls.Map;

/// <summary>The label beside the markers of one system (ET-403). It stays one short line however many characters stand
/// there — the popover names everyone — so a big group cannot grow into the region and system names around it.</summary>
public static class MapMarkerLabel
{
    /// <summary>One name, two names, or from three on "lead +N" with the fleet commander as lead when they are among the
    /// markers, otherwise the first one.</summary>
    public static string For(IReadOnlyList<string> names, string? commanderName)
    {
        if (names.Count <= 2)
            return string.Join(", ", names);

        string lead = commanderName is not null && names.Contains(commanderName) ? commanderName : names[0];
        return string.Create(CultureInfo.InvariantCulture, $"{lead} +{names.Count - 1}");
    }
}
