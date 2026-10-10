using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-498: Raymond dies in a fleet abyssal and Jithran carries on. Raymond's own gamelog sees nothing of the pocket any
/// more, so his window follows Jithran's rooms, clears and enemies off Jithran's run share, and says he is out while
/// Jithran is still inside — shown only, never saved into Raymond's run.
/// </summary>
public sealed class FleetRunAfterDeathTests
{
    private const int Ghost = 47700;
    private const int Deepwatcher = 47701;
    private const string GroupCode = "AB-D3AD";

    [AvaloniaFact]
    public async Task APilotWhoDied_SeesTheNextRoomItsClearAndItsEnemies_FromTheMateStillInside()
    {
        (Pilot jithran, Pilot raymond) = await _FleetAbyssalAsync();
        using (jithran)
        using (raymond)
        {
            DateTime inAt = DateTime.UtcNow;
            jithran.Window.Enemies().RecordCatchUpSighting(FleetOfTwo.JithranId, "Ghost Drone", inAt.AddSeconds(20));
            raymond.Window.Enemies().RecordCatchUpSighting(FleetOfTwo.RaymondId, "Ghost Drone", inAt.AddSeconds(21));
            await FleetOfTwo.RunJobsAsync();

            raymond.Window.StopRunCommand.Execute(null);
            jithran.Window.StartNewRoom(inAt.AddMinutes(1));
            jithran.Window.Enemies().RecordCatchUpSighting(FleetOfTwo.JithranId, "Deepwatcher", inAt.AddMinutes(1).AddSeconds(10));
            await FleetOfTwo.RunJobsAsync();
            await jithran.TickPastTheBundleWindowAsync();
            await _StillInsideAsync(raymond, FleetOfTwo.JithranId);

            raymond.Refresh();
            await _SettleAsync(() => raymond.Window.CurrentRoomText?.StartsWith("ROOM 2") == true);

            Assert.Equal(ActivityRunState.Stopped, raymond.Window.RunState);
            Assert.StartsWith("ROOM 2", raymond.Window.CurrentRoomText);
            Assert.EndsWith("Jithran", raymond.Window.CurrentRoomText);
            EnemiesWindowSectionViewModel enemies = raymond.Window.Enemies();
            Assert.Equal("Jithran", enemies.FleetMateName);
            Assert.Equal([2, 1], enemies.EnemyRooms.Select(room => room.Number));
            Assert.Equal(["Deepwatcher"], enemies.EnemyRooms[0].Observations.Select(row => row.EnemyName));
            Assert.Equal(["Ghost Drone"], enemies.EnemyRooms[1].Observations.Select(row => row.EnemyName));
            Assert.All(enemies.EnemyObservations, row => Assert.False(row.IsEditable));
            Assert.False(enemies.EnemyRooms[0].IsUndoShown);
            Assert.Equal("You are out — the fleet carries on. Still inside: Jithran.", raymond.Window.FleetCarriesOnText);

            RunSaveDraft draft = new(raymond.Window.RunId ?? Guid.Empty, FleetOfTwo.RaymondId, isActingRun: true);
            enemies.AddToSave(draft);
            // Raymond's own sighting only, in the room the commander's list put it in (ET-494) — none of Jithran's rows.
            Assert.Equal([(Ghost, (int?)1)], draft.Enemies.Select(row => (row.EnemyTypeId, row.RoomNumber)));
        }
    }

    /// <summary>The mate who is inside is followed, nobody else: a mate who is out too says nothing, and a pilot still
    /// running keeps their own view.</summary>
    [AvaloniaFact]
    public async Task NobodyLeftInside_OrStillRunning_KeepsThePilotsOwnView()
    {
        (Pilot jithran, Pilot raymond) = await _FleetAbyssalAsync();
        using (jithran)
        using (raymond)
        {
            jithran.Window.StartNewRoom(DateTime.UtcNow.AddSeconds(-30));
            await jithran.TickPastTheBundleWindowAsync();
            await _StillInsideAsync(raymond, FleetOfTwo.JithranId);
            raymond.Refresh();

            Assert.Null(raymond.Window.Enemies().FleetMateName);
            Assert.Null(raymond.Window.FleetCarriesOnText);

            raymond.Window.StopRunCommand.Execute(null);
            await raymond.Instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetMetricEvent(
                new MetricSample(FleetOfTwo.JithranId, FleetOfTwo.FleetId, MetricKind.Location, 30000142,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000, "Jita"), FleetOfTwo.JithranId), EventTarget.Local);
            await FleetOfTwo.RunJobsAsync();
            raymond.Refresh();

            Assert.Null(raymond.Window.Enemies().FleetMateName);
            Assert.Null(raymond.Window.FleetCarriesOnText);
        }
    }

    /// <summary>A client from before ET-498 reads a share with rooms and enemies as it always read one, and this client
    /// reads an older sender's share with neither as empty — through the registry, the way the wire hands it on.</summary>
    [Fact]
    public void TheRoomFields_AreIgnoredByAnOlderReader_AndEmptyFromAnOlderSender()
    {
        RunShareUpdate current = new(FleetOfTwo.FleetId, GroupCode, 1_000, SharesLoot: true, SharesBounty: false, 1,
            [new RunShareLootLine(FleetOfTwo.Tritanium, 5, LootKind.Gained)],
            RoomStartsUnixMs: [900], Enemies: [new RunShareEnemyLine("Deepwatcher", Deepwatcher, 2, 3)]);
        string currentJson = JsonSerializer.Serialize(current);

        RunShareUpdateBeforeEt498? older = JsonSerializer.Deserialize<RunShareUpdateBeforeEt498>(currentJson);
        Assert.NotNull(older);
        Assert.Equal((FleetOfTwo.FleetId, GroupCode, 1_000L, true, 1), (older.FleetId, older.GroupCode, older.UnixMs, older.SharesLoot, older.CaptureCount));
        Assert.Equal(new RunShareLootLine(FleetOfTwo.Tritanium, 5, LootKind.Gained), Assert.Single(older.Loot));

        string olderJson = JsonSerializer.Serialize(new RunShareUpdateBeforeEt498(FleetOfTwo.FleetId, GroupCode, 2_000,
            SharesLoot: true, SharesBounty: true, 0, [], SharesMining: false, 0, 0, []));
        EventTypeRegistry registry = new();
        new EveUtils.Shared.Modules.Fleet.FleetWireEvents().RegisterInto(registry);
        FleetRunShareEvent? arrived = registry.Deserialize("fleet.run-share", olderJson, FleetOfTwo.JithranId) as FleetRunShareEvent;

        Assert.NotNull(arrived);
        Assert.Empty(arrived.Data.RoomStartsUnixMs);
        Assert.Empty(arrived.Data.Enemies);
        Assert.Equal(2_000, arrived.Data.UnixMs);
    }

    /// <summary>The shape <see cref="RunShareUpdate"/> had before ET-498, as an older client still reads it.</summary>
    private sealed record RunShareUpdateBeforeEt498(long FleetId, string GroupCode, long UnixMs, bool SharesLoot,
        bool SharesBounty, int CaptureCount, IReadOnlyList<RunShareLootLine> Loot, bool SharesMining, int MinedUnits,
        int ResidueUnits, IReadOnlyList<RunShareMiningLine> Mining);

    private static async Task<(Pilot Jithran, Pilot Raymond)> _FleetAbyssalAsync()
    {
        static void Sde(IServiceCollection services) => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(FleetOfTwo.Tritanium, "Tritanium", 18, 4).Add(Ghost, "Ghost Drone", 100, 11).Add(Deepwatcher, "Deepwatcher", 100, 11));

        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran", Sde, ActivityKind.Abyssal);
        Pilot raymond = await Pilot.CreateAsync(FleetOfTwo.RaymondId, "Raymond", Sde, ActivityKind.Abyssal);
        jithran.Wire.Destinations.Add(raymond.Instance.Services);
        raymond.Wire.Destinations.Add(jithran.Instance.Services);

        RunGroupCodeStart start = new(FleetOfTwo.FleetId, ActivityKind.Abyssal, GroupCode, DateTime.UtcNow.AddMinutes(-2),
            IsFleetCommander: true);
        foreach (Pilot pilot in new[] { jithran, raymond })
        {
            pilot.Window.JoinFleetRun(start);
            await FleetOfTwo.RunJobsAsync();
            await pilot.Window.StartOnAbyssalEntryAsync(DateTime.UtcNow);
            pilot.Refresh();
        }

        await _SettleAsync(() => jithran.Window.FleetSharing.IsShown && raymond.Window.FleetSharing.IsShown);
        Assert.Equal(GroupCode, raymond.Window.GroupCode);
        return (jithran, raymond);
    }

    // The mate's own location sample with an abyssal anchor, as the 1 Hz metric stream brings it.
    private static async Task _StillInsideAsync(Pilot receiver, int mate)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await receiver.Instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetMetricEvent(
            new MetricSample(mate, FleetOfTwo.FleetId, MetricKind.Location, 0, nowMs, "Abyssal Deadspace",
                AbyssalAnchorMs: nowMs - 120_000), mate), EventTarget.Local);
        await FleetOfTwo.RunJobsAsync();
    }

    private static async Task _SettleAsync(Func<bool> until)
    {
        for (int attempt = 0; attempt < 100 && !until(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
