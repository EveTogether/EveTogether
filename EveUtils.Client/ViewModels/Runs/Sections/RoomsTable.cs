using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Telemetry;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>One room of ROOMS (ET-469). A null figure is one nobody has, which the screen shows as "—", never as a zero.</summary>
/// <param name="SpawnHp">Σ count × raw HP, or a lower bound (one per type seen) while <see cref="IsCounted"/> is false.</param>
/// <param name="Share">The picked pilot's damage ÷ spawn HP, only for a counted room.</param>
/// <param name="Overkill">All damage on the spawn ÷ spawn HP − 1, only for a counted room whose whole damage is known.</param>
/// <param name="LootIsk">Null without a capture in the room, or while a line of it has no fixed price.</param>
internal sealed record RoomRow(int Number, DateTime StartUtc, DateTime? EndUtc, int DurationSeconds, int? IdleSeconds,
    long? DamageOut, long? SpawnHp, bool IsCounted, double? Share, double? Overkill, decimal? LootIsk, bool HasLootCapture);

/// <summary>The rooms of a saved group on the room set ET-494 chose, read from the stored telemetry and the ENEMIES counts.</summary>
internal sealed record RoomsTable(IReadOnlyList<RoomRow> Rows, bool IsByHand, bool IsLootPerRoom)
{
    public int CountedRooms => Rows.Count(row => row.IsCounted);

    public static RoomsTable Of(ActivityDetailDto detail, RunRoomSet set, Guid? pilotRunId,
        IReadOnlyDictionary<Guid, RunCombatTimelineDto> timelines, Func<int, int?> rawHp)
    {
        ActivityRunDetailDto? pilot = detail.Runs.FirstOrDefault(run => run.RunId == pilotRunId);
        RunCombatTimelineDto? timeline = pilot is not null ? timelines.GetValueOrDefault(pilot.RunId) : null;
        DateTime start = pilot?.StartedAtUtc ?? set.Run.StartedAtUtc;
        DateTime? stop = pilot is not null
            ? pilot.StoppedAtUtc ?? (timeline is null ? null : start.AddSeconds(timeline.Seconds))
            : set.Run.StoppedAtUtc;
        int rooms = set.Boundaries.Count + 1;
        long[] damage = new long[rooms + 1];
        int[] idle = new int[rooms + 1];
        if (timeline is not null)
        {
            int[] dealt = timeline.Series.GetValueOrDefault(CombatSeriesKind.DmgOut) ?? [];
            bool[] quiet = CombatChartModel.IdleSeconds(timeline);
            for (int second = 0; second < timeline.Seconds; second++)
            {
                int room = RunRooms.RoomOf(set.Boundaries, start.AddSeconds(second)) ?? 1;
                damage[room] += second < dealt.Length ? dealt[second] : 0;
                idle[room] += quiet[second] ? 1 : 0;
            }
        }

        bool isSolo = detail.Runs.Select(run => run.CharacterId).Distinct().Count() <= 1;
        IReadOnlyDictionary<int, long>? fleetDamage = isSolo
            ? null
            : RunCombatTelemetry.FleetDamageOutByRoom(
                detail.Runs.Select(run => (run.StartedAtUtc, timelines.GetValueOrDefault(run.RunId))), set.Boundaries);
        bool isLootPerRoom = !detail.Runs.SelectMany(run => run.LootCaptures)
            .Any(capture => !capture.IsExcluded && capture.Role is LootCaptureRole.CargoBefore);
        Dictionary<int, List<(RunLootEntryDto Entry, decimal? Fixed)>> lootByRoom = [];
        if (isLootPerRoom)
        {
            foreach (ActivityRunDetailDto run in detail.Runs)
            {
                Dictionary<int, decimal> fixedPrices = FixedLootPrices.Of(run.LootCaptures);
                foreach (RunLootCaptureDto capture in run.LootCaptures
                             .Where(capture => !capture.IsExcluded && capture.Role is not LootCaptureRole.Consumed))
                {
                    int room = RunRooms.RoomOf(set.Boundaries, capture.CapturedAtUtc) ?? 1;
                    lootByRoom.TryAdd(room, []);
                    lootByRoom[room].AddRange(capture.Entries.Where(entry => entry.LootKind is LootKind.Gained)
                        .Select(entry => (entry, fixedPrices.TryGetValue(entry.ItemTypeId, out decimal kept) ? (decimal?)kept : null)));
                }
            }
        }

        Dictionary<int, List<RunEnemyObservationDto>> enemies = set.EnemiesByRoom(detail).ToDictionary(room => room.Room, room => room.Rows);
        List<RoomRow> rows = [];
        long edgeSeconds = 0;
        for (int room = 1; room <= rooms; room++)
        {
            DateTime from = RunRooms.StartOf(set.Boundaries, room, start);
            DateTime? to = RunRooms.EndOf(set.Boundaries, room, stop);
            long endSeconds = stop is null || to is null ? edgeSeconds : _Edge(to.Value, start, stop.Value);
            (long? spawnHp, bool isCounted) = _SpawnHp(enemies.GetValueOrDefault(room), rawHp);
            long? own = timeline is null ? null : damage[room];
            long? all = isSolo ? own : fleetDamage?.GetValueOrDefault(room);
            decimal? loot = null;
            bool hasLoot = lootByRoom.TryGetValue(room, out var lines) && lines.Count > 0;
            if (hasLoot && lines!.All(line => (line.Entry.UnitPriceIsk ?? line.Fixed) is not null))
            {
                loot = lines!.Sum(line => (line.Entry.UnitPriceIsk ?? line.Fixed)!.Value * (line.Entry.Quantity ?? 1));
            }

            rows.Add(new RoomRow(room, from, to, (int)(endSeconds - edgeSeconds), timeline is null ? null : idle[room], own, spawnHp,
                isCounted, isCounted && own is not null && spawnHp > 0 ? (double)own.Value / spawnHp : null,
                isCounted && all is not null && spawnHp > 0 ? (double)all.Value / spawnHp - 1 : null, loot, hasLoot));
            edgeSeconds = endSeconds;
        }

        bool isByHand = detail.Parameters.Any(parameter => parameter.RunId == set.Run.RunId
                                                           && parameter.ParameterKey is RunParameterKey.RoomStarted);
        return new RoomsTable(rows, isByHand, isLootPerRoom);
    }

    // Whole seconds from the pilot's start, clamped into the run, so the rooms add up to the run's own length.
    private static long _Edge(DateTime at, DateTime start, DateTime stop) =>
        (long)Math.Round((Min(Max(at, start), stop) - start).TotalSeconds);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static (long? SpawnHp, bool IsCounted) _SpawnHp(List<RunEnemyObservationDto>? seen, Func<int, int?> rawHp)
    {
        if (seen is not { Count: > 0 })
        {
            return (null, false);
        }

        long total = 0;
        bool isCounted = true;
        foreach (RunEnemyObservationDto enemy in seen)
        {
            if (rawHp(enemy.EnemyTypeId) is not { } hp)
            {
                isCounted = false;
                continue;
            }

            // A type nobody counted is at least one of it.
            isCounted &= enemy.Count > 0;
            total += (long)hp * Math.Max(1, enemy.Count);
        }

        return (total > 0 ? total : null, isCounted && total > 0);
    }
}
