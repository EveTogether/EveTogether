using System;
using System.Collections.Generic;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>The words for a fleet member's standing (ET-440), one place for every screen that shows them.</summary>
public static class FleetMemberStatusText
{
    /// <summary>The line under a member's name. <paramref name="locationText"/> is how the screen already words a known
    /// system (abyssal space, the OPSEC mark), used as is.</summary>
    public static string Line(FleetMateStatus standing, string? locationText, DateTimeOffset now) => standing.Reason switch
    {
        FleetMemberStatusReason.InSystem => locationText ?? OpsecText.Mark(standing.System) ?? "in game",
        FleetMemberStatusReason.NoSystemYet => "no system yet",
        FleetMemberStatusReason.LocationWithheld => "keeps their location private",
        FleetMemberStatusReason.NotInGame => "not in game",
        FleetMemberStatusReason.Silent => $"EVE Together closed{_Seen(standing.LastSeenAt, now, " · last seen ")}",
        FleetMemberStatusReason.NotConnected => "not connected to the server",
        FleetMemberStatusReason.NeverHeard => "nothing received yet",
        FleetMemberStatusReason.OldClient => "older EVE Together, cannot tell where",
        _ => string.Empty,
    };

    /// <summary>The chips beside the line: connection, run, what is shared, when last heard.</summary>
    public static IReadOnlyList<FleetStatusChip> Chips(FleetMateStatus standing, bool? isInRun, DateTimeOffset now)
    {
        List<FleetStatusChip> chips = [];
        if (standing.IsConnected is { } connected)
            chips.Add(connected ? new FleetStatusChip("connected", IsOk: true) : new FleetStatusChip("no server link", IsWarn: true));
        if (isInRun is { } inRun)
            chips.Add(inRun ? new FleetStatusChip("in run", IsOk: true) : new FleetStatusChip("not in run", IsWarn: true));
        if (standing.Shares is { } shares)
            chips.Add(new FleetStatusChip(shares is SharedMetrics.None ? "shares nothing" : $"shares {_Shared(shares)}"));
        if (_Seen(standing.LastSeenAt, now, "seen ") is { Length: > 0 } seen)
            chips.Add(new FleetStatusChip(seen));
        return chips;
    }

    private static string _Shared(SharedMetrics shares)
    {
        List<string> names = [];
        if (shares.HasFlag(SharedMetrics.Location)) names.Add("location");
        if (shares.HasFlag(SharedMetrics.Combat)) names.Add("combat");
        if (shares.HasFlag(SharedMetrics.Bounty)) names.Add("bounty");
        if (shares.HasFlag(SharedMetrics.Loot)) names.Add("loot");
        if (shares.HasFlag(SharedMetrics.Mining)) names.Add("mining");
        return string.Join(" · ", names);
    }

    private static string _Seen(DateTimeOffset? at, DateTimeOffset now, string prefix) =>
        at is { } seen ? $"{prefix}{MapFleetBadge.Ago(now - seen)}" : string.Empty;
}
