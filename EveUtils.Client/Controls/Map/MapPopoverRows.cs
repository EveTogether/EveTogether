using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;

namespace EveUtils.Client.Controls.Map;

/// <summary>One piece of a popover line in one colour.</summary>
public sealed record MapPopoverRun(string Text, Color Colour, bool IsBold = false);

/// <summary>The lines of a system's hover popover (ET-399), in the order they are drawn. Colours are the map's own
/// (<see cref="MapPalette"/>) except the commander, who is the faction's AccentBright like every other map overlay.</summary>
public static class MapPopoverRows
{
    public const int MaxNeighbours = 6;
    public const int MaxOccupants = 15;

    /// <param name="maxOccupants">How many of the people here are named before "+N more here"; the map passes fewer when
    /// the full list would be taller than the map itself.</param>
    public static IReadOnlyList<IReadOnlyList<MapPopoverRun>> From(MapSystemInfo info, DateTimeOffset now, Color accent, int maxOccupants = MaxOccupants)
    {
        var rows = new List<IReadOnlyList<MapPopoverRun>>
        {
            new MapPopoverRun[]
            {
                new(info.Name + " ", MapPalette.Text, true),
                new(info.Security.ToString("0.0", CultureInfo.InvariantCulture), MapPalette.SecurityColour(info.Security), true)
            },
            new MapPopoverRun[]
            {
                new(info.Constellation + " · ", MapPalette.Muted),
                new(info.Region, MapPalette.Regions[info.RegionColourIndex % MapPalette.Regions.Count])
            }
        };
        if (info.Faction is { } faction)
            rows.Add([new MapPopoverRun(faction, MapPalette.Muted)]);
        if (info.RouteStep is { } step)
            rows.Add([new MapPopoverRun($"On the route · step {step} of {info.RouteLength}", MapPalette.Route)]);

        foreach (MapSystemDistance distance in info.Distances)
            rows.Add([_DistanceRun(distance)]);

        if (info.Here.Count > 0)
            rows.Add([new MapPopoverRun(string.Create(CultureInfo.InvariantCulture, $"Here · {info.Here.Count}"), MapPalette.Muted, true)]);
        foreach (MapSystemOccupant occupant in info.Here.Take(maxOccupants))
            rows.Add(_OccupantRuns(occupant, now, accent));
        if (info.Here.Count > maxOccupants)
            rows.Add([new MapPopoverRun($"+{info.Here.Count - maxOccupants} more here", MapPalette.Muted)]);

        rows.Add(_GateRuns(info.Neighbours));
        return rows;
    }

    public static string TextOf(IReadOnlyList<MapPopoverRun> row) => string.Concat(row.Select(run => run.Text));

    private static MapPopoverRun _DistanceRun(MapSystemDistance distance) => distance switch
    {
        { IsPending: true } => new($"Counting jumps from {distance.From}…", MapPalette.Muted),
        { Jumps: null } => new($"No route from {distance.From}", MapPalette.Muted),
        { Jumps: 1 } => new($"1 jump from {distance.From}", MapPalette.Text),
        _ => new(string.Create(CultureInfo.InvariantCulture, $"{distance.Jumps} jumps from {distance.From}"), MapPalette.Text)
    };

    private static List<MapPopoverRun> _OccupantRuns(MapSystemOccupant occupant, DateTimeOffset now, Color accent)
    {
        bool isCommander = occupant.Kind == MapOccupantKind.Commander;
        var runs = new List<MapPopoverRun>();
        if (isCommander)
            runs.Add(new MapPopoverRun("FC · ", accent, true));
        runs.Add(new MapPopoverRun(occupant.Name, isCommander ? accent : MapPalette.Text));
        if (occupant.ObservedAt is { } seen)
            runs.Add(new MapPopoverRun(" · " + MapFleetBadge.Ago(now - seen), MapPalette.Muted));
        return runs;
    }

    private static List<MapPopoverRun> _GateRuns(IReadOnlyList<string> neighbours)
    {
        if (neighbours.Count == 0)
            return [new MapPopoverRun("No gates", MapPalette.Muted)];

        string names = string.Join(", ", neighbours.Take(MaxNeighbours));
        if (neighbours.Count > MaxNeighbours)
            names += $", +{neighbours.Count - MaxNeighbours} more";
        return
        [
            new MapPopoverRun(neighbours.Count == 1 ? "1 stargate · " : $"{neighbours.Count} stargates · ", MapPalette.Text),
            new MapPopoverRun(names, MapPalette.Muted)
        ];
    }
}
