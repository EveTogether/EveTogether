using System;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// What the fit browser orders its cards by. The three the table's column headers were actually used on, which is
/// what ET-112 had to give back once those headers went: the fit's own name, what it is worth, and what class of
/// hull it is.
/// </summary>
public enum FitSortOrder
{
    /// <summary>The fit's name — the default, and the tie-breaker under the other two.</summary>
    Name = 0,

    /// <summary>The estimated fit value. Loaded lazily, so see <see cref="FitBrowserTabViewModel"/> for what a row
    /// without a price yet does.</summary>
    Price = 1,

    /// <summary>The hull's class from the SDE ("Cruiser", "Combat Battlecruiser").</summary>
    HullClass = 2
}

/// <summary>The browser's chosen order, and the one place the stored form of it is written and read.</summary>
public sealed record FitSortChoice(FitSortOrder Order, bool Descending)
{
    /// <summary>The setting value, e.g. <c>price:desc</c>.</summary>
    public string ToSetting() => $"{Order.ToString().ToLowerInvariant()}:{(Descending ? "desc" : "asc")}";

    /// <summary>Reads back <see cref="ToSetting"/>, or null for anything this client does not recognise — a value
    /// written by a newer client then leaves the default standing instead of throwing.</summary>
    public static FitSortChoice? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split(':');
        return Enum.TryParse<FitSortOrder>(parts[0], ignoreCase: true, out var order)
            ? new FitSortChoice(order, parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
