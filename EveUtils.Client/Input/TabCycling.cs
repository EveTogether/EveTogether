using System.Collections.Generic;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Input;

/// <summary>
/// The tab-selection arithmetic behind Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1..9 (ET-209) — pulled out of
/// <c>MainWindow</c>'s key handling so it is plain, Avalonia-window-free logic a unit test can drive directly.
/// </summary>
public static class TabCycling
{
    /// <summary>The tab after <paramref name="current"/>, wrapping past the last. Null (no tabs, or the current tab
    /// is not among them) selects the first.</summary>
    public static HostTab? Next(IReadOnlyList<HostTab> tabs, HostTab? current)
    {
        if (tabs.Count == 0) return null;
        var index = current is null ? -1 : _IndexOf(tabs, current);
        return tabs[(index + 1 + tabs.Count) % tabs.Count];
    }

    /// <summary>The tab before <paramref name="current"/>, wrapping past the first.</summary>
    public static HostTab? Previous(IReadOnlyList<HostTab> tabs, HostTab? current)
    {
        if (tabs.Count == 0) return null;
        var index = current is null ? -1 : _IndexOf(tabs, current);
        if (index < 0) index = 0; // no selection, or a stale reference — wrap to the last tab either way
        return tabs[(index - 1 + tabs.Count) % tabs.Count];
    }

    // Reference equality: "current" is the tab instance the host is actually showing, not a value-alike stand-in.
    // -1 (not found — e.g. the selected tab closed from under it) folds into Next/Previous the same as "no selection".
    private static int _IndexOf(IReadOnlyList<HostTab> tabs, HostTab tab)
    {
        for (var i = 0; i < tabs.Count; i++)
            if (ReferenceEquals(tabs[i], tab)) return i;
        return -1;
    }

    /// <summary>The <paramref name="oneBasedIndex"/>-th tab (Ctrl+1 = 1, …), or null if there are fewer tabs than that.</summary>
    public static HostTab? At(IReadOnlyList<HostTab> tabs, int oneBasedIndex) =>
        oneBasedIndex >= 1 && oneBasedIndex <= tabs.Count ? tabs[oneBasedIndex - 1] : null;

    /// <summary>The last tab (Ctrl+9, browser convention — not necessarily the 9th).</summary>
    public static HostTab? Last(IReadOnlyList<HostTab> tabs) => tabs.Count == 0 ? null : tabs[^1];
}
