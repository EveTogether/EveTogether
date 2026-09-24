using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One day's worth of killmail rows under the KILLMAILS overview's day header, newest first — a flat list
/// like <c>RunsDayViewModel</c>'s, not an expander (ET-332).</summary>
public sealed class KillmailDayViewModel(DateOnly day, IReadOnlyList<KillmailRowViewModel> rows)
{
    public DateOnly Day { get; } = day;

    public string WeekdayText { get; } = day.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant();

    public string DateText { get; } = day.ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    public IReadOnlyList<KillmailRowViewModel> Rows { get; } = rows;

    public int KillCount { get; } = rows.Count(row => !row.IsLoss);

    public int LossCount { get; } = rows.Count(row => row.IsLoss);

    public string KillCountText => KillCount == 1 ? "1 kill" : $"{KillCount} kills";

    public string LossCountText => LossCount == 1 ? "1 loss" : $"{LossCount} losses";

    private decimal NetIsk { get; } = rows.Sum(row => row.Isk ?? 0m);

    public string NetIskText => IskFormat.Compact(NetIsk);

    public bool NetIskIsLoss => NetIsk < 0;
}
