using System.Text.Json;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Telemetry;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class EveWorkbenchRunImportTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("fields")]
    [InlineData("mining")]
    [InlineData("withheld")]
    [InlineData("other-pilots")]
    public void From_MapsOntoTheEveWorkbenchContract(string behaviour)
    {
        bool withheld = behaviour == "withheld";
        RunWirePayload payload = new() { SentAtUnixMilliseconds = new DateTimeOffset(Start.AddHours(1)).ToUnixTimeMilliseconds(), Run = RunWireData.FromEntity(_Run(), _Timeline(), withheld) };

        (EveWorkbenchImportedRun run, int skippedOreLines) = EveWorkbenchRunImport.From(payload, new FakeSdeAccessor().Add(1230, "Veldspar", 462, 25));
        string json = JsonSerializer.Serialize(new EveWorkbenchRunImportRequest { Runs = [run] });

        switch (behaviour)
        {
            case "fields":
                Assert.Contains("\"Source\":\"EVE Together\"", json);
                Assert.Equal((EveWorkbenchRunKind.Mission, 4321, 150m, 25m), (run.Kind, run.DungeonId, run.IskRewards, run.IskBounties));
                Assert.Equal((3, EveWorkbenchAbyssWeather.Dark), (run.AbyssTier, run.AbyssWeather));
                Assert.Equal(("FLEET1", true, 5), (run.Group!.Code, run.Group.IsOrganiser, run.Group.ParticipantCount));
                Assert.Equal([Start.AddMinutes(5)], run.RoomStartsUtc);
                Assert.Equal([0, 300, 0], run.Combat!.Series.Single().PerSecond);
                Assert.Equal(EveWorkbenchHitQuality.Smashes, run.Combat.HitTallies[0].Quality);
                Assert.Contains(run.Loot, loot => loot is { TypeId: 34, Quantity: 7, Lost: false });
                break;
            case "mining":
                Assert.Contains(run.Loot, loot => loot is { TypeId: 1230, Quantity: 100 });
                Assert.Equal(1, skippedOreLines);
                break;
            case "withheld":
                Assert.True(run.CombatWithheld);
                Assert.Null(run.Combat);
                break;
            case "other-pilots":
                Assert.DoesNotContain("Fleetmate Bob", json);
                Assert.DoesNotContain("90000077", json);
                Assert.DoesNotContain("Rival[", json);
                break;
        }
    }

    private static Run _Run()
    {
        var run = new Run
        {
            Id = Guid.NewGuid(), CharacterId = 90000001, ActivityKind = ActivityKind.Site, SiteTypeSource = SiteTypeSource.Mission, SiteTypeId = 4321,
            StartedAtUtc = Start, StoppedAtUtc = Start.AddMinutes(10), GroupCode = "FLEET1", IsParticipant = true, Role = RunRole.FleetCommander,
            AttendanceCount = 5, AttendanceSetByCharacterId = 90000077, Revision = 2
        };
        run.Parameters.Add(new RunParameter { ParameterKey = RunParameterKey.Isk, TypedValue = "100", Amount = 100, ObservedAtUtc = Start });
        run.Parameters.Add(new RunParameter { ParameterKey = RunParameterKey.Escrow, TypedValue = "50", ObservedAtUtc = Start });
        run.Parameters.Add(new RunParameter { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "3|Dark", ObservedAtUtc = Start });
        run.Parameters.Add(new RunParameter { ParameterKey = RunParameterKey.RoomStarted, TypedValue = "", ObservedAtUtc = Start.AddMinutes(5) });
        run.BountyEntries.Add(new RunBountyEntry { OccurredAtUtc = Start, Isk = 25 });
        run.AttendanceEntries.Add(new RunAttendanceEntry { CharacterId = 90000077, CharacterName = "Fleetmate Bob", IsInSite = true });
        var capture = new RunLootCapture { CapturedAtUtc = Start.AddMinutes(9) };
        capture.Entries.Add(new RunLootEntry { ItemTypeId = 34, Name = "Tritanium", Quantity = 7, LootKind = LootKind.Gained });
        run.LootCaptures.Add(capture);
        run.MiningEntries.Add(new RunMiningEntry { OreType = "Veldspar", Units = 100, FirstObservedAtUtc = Start, LastObservedAtUtc = Start.AddMinutes(8) });
        run.MiningEntries.Add(new RunMiningEntry { OreType = "Unobtainium", Units = 50, FirstObservedAtUtc = Start, LastObservedAtUtc = Start.AddMinutes(8) });
        return run;
    }

    private static RunCombatTimeline _Timeline() => new()
    {
        Seconds = 3, MaxHitIn = 300, MaxHitInSource = "Rival[CORP](Rifter)",
        Series = [new RunCombatSeries { Kind = CombatSeriesKind.DmgIn, Samples = RunCombatTelemetry.Encode([0, 300, 0]) }],
        HitTallies = [new RunHitTally { Direction = DamageDirection.Incoming, Counterparty = "Rival[CORP](Rifter)", Quality = HitQuality.Smashes, Count = 1, Sum = 300, Min = 300, Max = 300 }]
    };
}
