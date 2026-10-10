using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EveUtils.Client.Composition;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-469: what the ROOMS table works out per room, on the stored boundaries, telemetry and counts.</summary>
public sealed class RoomsTableTests
{
    private static readonly DateTime Start = new(2026, 9, 18, 18, 29, 3, DateTimeKind.Utc);
    private static readonly Guid Pilot = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Mate = Guid.Parse("00000000-0000-0000-0000-000000000002");

    /// <summary>ET-469 AC1-AC4. Counter-proofs: count a RoomDetected beside the RoomStarted (4 rooms); use EHP (or the
    /// count) for the HP; show an overkill from the pilot's own damage in a fleet run; let a room's seconds drift.</summary>
    [Theory]
    [InlineData("hand-wins", "1:200s 1000 hp=;2:200s 2000 hp=;3:200s 3000 hp=")]
    [InlineData("uncounted", "1:200s 1000 hp=7800+ sh=- ok=-;2:200s 2000 hp=;3:200s 3000 hp=")]
    [InlineData("counted-solo", "1:200s 1000 hp=15600 sh=0.064 ok=-0.936;2:200s 2000 hp=;3:200s 3000 hp=")]
    [InlineData("fleet-mate-not-shared", "1:200s 1000 hp=15600 sh=0.064 ok=-;2:200s 2000 hp=;3:200s 3000 hp=")]
    [InlineData("fleet-mate-shared", "1:200s 1000 hp=15600 sh=0.064 ok=0;2:200s 2000 hp=;3:200s 3000 hp=")]
    public void Rooms_AreReadOnTheHandBoundaries_WithRawHpShareAndOverkill(string scenario, string expected)
    {
        int count = scenario == "uncounted" ? 0 : 2;
        bool isFleet = scenario.StartsWith("fleet", StringComparison.Ordinal);
        List<ActivityRunDetailDto> runs = [_Run(Pilot, 90000001)];
        Dictionary<Guid, RunCombatTimelineDto> timelines = new() { [Pilot] = _Timeline(1000, 2000, 3000) };
        if (isFleet)
        {
            runs.Add(_Run(Mate, 90000002));
            if (scenario == "fleet-mate-shared")
            {
                timelines[Mate] = _Timeline(14600, 0, 0);
            }
        }

        RunParameterDto Boundary(RunParameterKey key, int second) => new(Pilot, key, string.Empty, null, null, null, Start.AddSeconds(second));
        RunEnemyObservationDto[] seen = scenario == "hand-wins"
            ? []
            : [new(Pilot, 1, "Strikegrip Tessera", count, Start.AddSeconds(5), Start.AddSeconds(50))];
        ActivityDetailDto detail = new(Guid.NewGuid(), null, ActivityKind.Abyssal, null, null, 0, null, Start, Start.AddSeconds(600),
            600, null, null, null, 0, 0, runs.Count, runs.Count, runs, [], seen,
            [Boundary(RunParameterKey.RoomStarted, 200), Boundary(RunParameterKey.RoomStarted, 400), Boundary(RunParameterKey.RoomDetected, 100)],
            [], new IskBreakdown([]));

        RoomsTable table = RoomsTable.Of(detail, RunRoomSet.Of(detail)!, Pilot, timelines, typeId => typeId == 1 ? 7800 : null);

        static string Show(RoomRow row)
        {
            string text = FormattableString.Invariant($"{row.Number}:{row.DurationSeconds}s {row.DamageOut} hp={row.SpawnHp}")
                + (row.SpawnHp is not null && !row.IsCounted ? "+" : "");
            return row.SpawnHp is null
                ? text
                : text + FormattableString.Invariant($" sh={row.Share?.ToString("0.###") ?? "-"} ok={row.Overkill?.ToString("0.###") ?? "-"}");
        }

        Assert.Equal(expected, string.Join(";", table.Rows.Select(Show)));
        Assert.Equal((600, 6000), (table.Rows.Sum(row => row.DurationSeconds), table.Rows.Sum(row => row.DamageOut ?? 0)));
    }

    /// <summary>ET-469 AC2: the HP a room is measured against is the raw shield + armor + hull of the SDE, not the EHP.
    /// Skips without an SDE store, like the e-war cross-check.</summary>
    [Theory]
    [InlineData("Strikegrip Tessera", 7800)]
    [InlineData("Snarecaster Tessella", 1000)]
    public void GetNpcRawHp_IsShieldArmorAndHull_NotEhp(string name, int expected)
    {
        string path = Path.Combine(ClientDataLocation.DefaultRoot, "sde", "sde.sqlite");
        if (!File.Exists(path) || new SqliteSdeAccessor(path) is not { IsAvailable: true } sde)
        {
            return;
        }

        Assert.Equal(expected, sde.GetNpcRawHp(sde.SearchNpcEnemies(name).First(enemy => enemy.Name == name).TypeId));
    }

    private static ActivityRunDetailDto _Run(Guid id, long character) =>
        new(id, character, RunRole.Member, true, true, Start, Start.AddSeconds(600), null, null, null, null, null, []);

    // Damage once at second 10, 250 and 450: one hit in each of the three rooms.
    private static RunCombatTimelineDto _Timeline(int first, int second, int third)
    {
        int[] damage = new int[600];
        (damage[10], damage[250], damage[450]) = (first, second, third);
        return new RunCombatTimelineDto(600, new Dictionary<CombatSeriesKind, int[]> { [CombatSeriesKind.DmgOut] = damage },
            0, null, 0, null, 0, 0, 0, 0, []);
    }
}
