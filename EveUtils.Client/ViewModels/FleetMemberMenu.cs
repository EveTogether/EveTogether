using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The fleet-member context menu, defined once. Every screen that shows a pilot — fleet metrics in all three of its
/// densities, the roster tree and its member list, the fleets window — builds its menu here, so what a right-click on
/// a pilot offers is one decision rather than one per screen. The information lines come first and the destructive
/// action comes last, well away from where the pointer just pressed a button on the row.
/// </summary>
public static class FleetMemberMenu
{
    /// <summary>
    /// Builds the menu for one member. <paramref name="now"/> is passed in rather than read so the "last update"
    /// line is testable and the caller decides when the age is measured (the menu is rebuilt as it opens, so the age
    /// is fresh). <paramref name="removeCommand"/> is null for anyone who may not remove this member — a plain member
    /// gets the information, not the action.
    /// </summary>
    public static IReadOnlyList<FleetMemberMenuItemViewModel> Build(
        FleetMemberFacts facts, DateTimeOffset now, IRelayCommand? removeCommand = null)
    {
        List<FleetMemberMenuItemViewModel> items =
        [
            new(facts.MemberName, tooltip: "The pilot this row stands for."),
            new(_PositionLine(facts), tooltip: "Where this pilot sits in the EVE Together fleet structure."),
            new(_ShipLine(facts), tooltip: "The fit this pilot is assigned to fly in this fleet."),
            new(_LocationLine(facts), tooltip: "Location sharing is opt-in per pilot; \"not sharing\" is a choice, not a fault."),
            new(_LastUpdateLine(facts, now), tooltip: "How long ago this pilot's client last published a metric sample."),
        ];

        if (removeCommand is not null)
            items.Add(new FleetMemberMenuItemViewModel(
                $"Remove {facts.MemberName} from the fleet…", removeCommand,
                "Removes the pilot from the EVE Together fleet. The in-game fleet is only touched if you confirm that separately."));

        return items;
    }

    private static string _PositionLine(FleetMemberFacts facts)
    {
        string role = facts.Role switch
        {
            FleetRole.FleetCommander => "Fleet Commander",
            FleetRole.WingCommander => "Wing Commander",
            FleetRole.SquadCommander => "Squad Commander",
            FleetRole.SquadMember => "Squad Member",
            _ => "Unassigned"
        };
        return facts.IsExternal ? $"{role} · external pilot" : role;
    }

    // The hull is what an FC steers on ("who is in a logi"); the fit name alone rarely names it, so show both when
    // the SDE can resolve the hull and fall back to whichever half is known.
    private static string _ShipLine(FleetMemberFacts facts) => (facts.ShipName, facts.FitName) switch
    {
        ({ } ship, { } fit) => $"Flying {ship} — {fit}",
        ({ } ship, null) => $"Flying {ship}",
        (null, { } fit) => $"Flying {fit}",
        _ => "No fit assigned"
    };

    private static string _LocationLine(FleetMemberFacts facts) => facts.Location switch
    {
        { Length: > 0 } system when facts.IsWithCommander => $"In {system} — with the FC",
        { Length: > 0 } system => $"In {system}",
        _ => "Not sharing location"
    };

    private static string _LastUpdateLine(FleetMemberFacts facts, DateTimeOffset now)
    {
        if (!facts.TracksLiveMetrics)
            return "Live metrics aren't tracked on this screen";
        if (facts.LastSampleAt is not { } last)
            return "No live data yet";

        TimeSpan age = now - last;
        return age switch
        {
            { TotalSeconds: < 5 } => "Last update just now",
            { TotalMinutes: < 1 } => $"Last update {_Whole(age.TotalSeconds)}s ago",
            { TotalHours: < 1 } => $"Last update {_Whole(age.TotalMinutes)}m ago",
            _ => $"Last update {_Whole(age.TotalHours)}h ago"
        };
    }

    // Invariant on purpose: a readout must not follow the operating system's locale (ET-34).
    private static string _Whole(double value) =>
        ((long)value).ToString(CultureInfo.InvariantCulture);
}
