using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>One room's TARGETS: the faction line and its note, then the rows in shooting order. The title is null
/// while the run has no rooms, so the one group has no header of its own.</summary>
public sealed record TargetRoomViewModel(string? Title, string? FactionText, string? Note, IReadOnlyList<TargetRow> Rows);

/// <summary>
/// TARGETS in the abyssal run window (ET-369): per room the enemy types this pilot's collector saw, with e-war and
/// range, signature and EHP from the SDE (ET-367), in the order to shoot them. Reads ENEMIES' collectors and writes
/// nothing; a saved run shows ENEMIES only.
/// </summary>
public sealed class TargetsWindowSectionViewModel : RunWindowSection
{
    private readonly EnemiesWindowSectionViewModel? _enemies;
    private readonly Dictionary<string, TargetRow?> _rowByName = new(StringComparer.OrdinalIgnoreCase);

    public TargetsWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Targets, "TARGETS")
    {
        _enemies = context.SectionOf(RunSectionId.Enemies) as EnemiesWindowSectionViewModel;
        if (_enemies is not null)
        {
            _enemies.SightingsChanged += _Rebuild;
        }

        _Rebuild();
    }

    /// <summary>Newest room first, like ENEMIES; a single untitled group while the run has no rooms.</summary>
    public IReadOnlyList<TargetRoomViewModel> Rooms { get; private set; } = [];

    /// <summary>The sightings grouped per room, each group built and ordered by the two functions given. A type seen
    /// in two rooms is in both; a room is only what was seen in it. <paramref name="toRow"/> returns null for a
    /// sighting that is no target.</summary>
    public static IReadOnlyList<TargetRoomViewModel> GroupByRoom(IEnumerable<TargetSighting> sightings,
        Func<TargetSighting, TargetRow?> toRow) =>
    [
        .. sightings
            .GroupBy(sighting => sighting.Room)
            .OrderByDescending(group => group.Key)
            .Select(group => (group.Key, Rows: TargetOrdering.Order(group.DistinctBy(sighting => sighting.Name)
                .Select(toRow).OfType<TargetRow>())))
            .Where(group => group.Rows.Count > 0)
            .Select(group => _Room(group.Key, group.Rows))
    ];

    public override void RefreshSummary()
    {
        if (Rooms.FirstOrDefault()?.Rows.FirstOrDefault() is not { } first)
        {
            HeaderSummary = Context.RunState == ActivityRunState.NotStarted ? "no run watched yet" : "no enemies seen yet";
            return;
        }

        HeaderSummary = $"1 {first.Name} · {(first.Ewar.FirstOrDefault()?.Text.ToLowerInvariant() ?? "no e-war")}";
    }

    public override void OnRunStarted() => _Rebuild();

    public override void Dispose()
    {
        if (_enemies is not null)
        {
            _enemies.SightingsChanged -= _Rebuild;
        }

        base.Dispose();
    }

    private void _Rebuild()
    {
        Rooms = GroupByRoom(_enemies?.TargetSightings() ?? [], _ToRow);
        OnPropertyChanged(nameof(Rooms));
        RefreshSummary();
    }

    private static TargetRoomViewModel _Room(int? room, IReadOnlyList<TargetRow> rows)
    {
        AbyssalNpcFaction? faction = AbyssalNpcKnowledge.Faction(rows.Select(row => row.Name));
        return new TargetRoomViewModel(room is { } number ? $"ROOM {number}" : null,
            faction is { } known ? known == AbyssalNpcFaction.Mixed ? "mixed" : Regex.Replace(known.ToString(), "(?<=[a-z])(?=[A-Z])", " ") : null,
            faction is { } noted && AbyssalNpcKnowledge.FactionNotes.TryGetValue(noted, out string? note) ? note : null, rows);
    }

    private TargetRow? _ToRow(TargetSighting sighting)
    {
        if (!_rowByName.TryGetValue(sighting.Name, out TargetRow? row))
        {
            _rowByName[sighting.Name] = row = RowFor(sighting, Context.Services.GetService<ISdeAccessor>());
        }

        return row;
    }

    // The SDE first, then the table's Tyrannos agents, else a "?" row. A typed enemy outside the abyssal groups (the
    // Triglavian cache) is no target at all.
    internal static TargetRow? RowFor(TargetSighting sighting, ISdeAccessor? sde) =>
        sighting.TypeId is { } typeId
            ? sde?.GetType(typeId) is { } type && AbyssalRoomDetector.IsAbyssalEnemyGroup(type.GroupId)
                ? sde.GetNpcEwarProfile(typeId) is { } profile ? _FromProfile(sighting.Name, profile) : _Unknown(sighting.Name)
                : null
            : AbyssalNpcKnowledge.TyrannosAgentByName(sighting.Name) is { } agent
                ? new TargetRow(sighting.Name, [.. agent.KnownEwar.Select(kind => new TargetEwar(kind, null, true))], null, null, true)
                : _Unknown(sighting.Name);

    private static TargetRow _Unknown(string name) => new(name, [], null, null, false);

    private static TargetRow _FromProfile(string name, NpcEwarProfile profile)
    {
        (NpcEwarKind Kind, double? Range)[] ranges =
        [
            (NpcEwarKind.Scram, profile.ScrambleRange), (NpcEwarKind.Neut, profile.NeutralizerRange),
            (NpcEwarKind.Web, profile.WebifierRange), (NpcEwarKind.Damp, profile.SensorDampenerRange),
            (NpcEwarKind.TrackingDisrupt, profile.TrackingDisruptorRange), (NpcEwarKind.GuidanceDisrupt, profile.GuidanceDisruptorRange),
            (NpcEwarKind.Paint, profile.TargetPainterRange), (NpcEwarKind.RemoteRepair, profile.RemoteArmorRepairerRange),
            (NpcEwarKind.Vorton, profile.VortonRange)
        ];
        return new TargetRow(name, [.. ranges.Where(pair => pair.Range is not null).Select(pair => new TargetEwar(pair.Kind, pair.Range, false))],
            profile.Ehp, profile.SignatureRadius, true);
    }
}
