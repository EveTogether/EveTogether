using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-494: in a fleet abyssal the commander's rooms, tier and weather are the fleet's, and only the commander's
/// count — the sender is the one the server attached.</summary>
public class FleetRoomSyncTests
{
    private static readonly DateTime T0 = DateTime.UtcNow.AddMinutes(-1);

    /// <summary>Jithran commands, Raymond flies with him. The result reads "J:Jithran's rooms R:Raymond's rooms badge
    /// detecting tier". Counter-proof: drop the sender check and the two "-from-member" rows go red; let a member keep
    /// his own rooms over the list and "commander-undo" keeps a room.</summary>
    [AvaloniaTheory]
    [InlineData("abyssal-from-commander", "J: R: - detecting tier3")]
    [InlineData("abyssal-from-member", "J: R: - detecting tier-")]
    [InlineData("rooms-from-member", "J: R: - detecting tier-")]
    [InlineData("member-detects", "J:+30 R:+30 FC detecting tier-")]
    [InlineData("commander-new-room", "J:+90 R:+90 FC stopped tier-")]
    [InlineData("commander-undo", "J: R: - stopped tier-")]
    [InlineData("commander-offline", "J:+30 R:+30,+200 FC detecting tier-")]
    public async Task FleetAbyssal_RoomsAndPocketAreTheCommanders(string scenario, string expected)
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Abyssal, configure: services =>
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(FleetOfTwo.Tritanium, "Tritanium", 18, 4)
                .Add(48092, "Striking Damavik", 1982, 11).Add(48087, "Starving Vedmak", 1982, 11)
                .Add(48235, "Ephialtes Entangler", 1982, 11)));
        // Each pilot times their own way into the pocket (ET-243).
        await fleet.Jithran.Window.StartRunCommand.ExecuteAsync(null);
        await fleet.Raymond.Window.StartOnAbyssalEntryAsync(DateTime.UtcNow);
        await fleet.SettleAsync(() => fleet.Jithran.Window.RunState is ActivityRunState.Running
                                      && fleet.Raymond.Window.RunState is ActivityRunState.Running);
        IEventBus memberBus = fleet.Raymond.Instance.Services.GetRequiredService<IEventBus>();
        EnemiesWindowSectionViewModel jithran = fleet.Jithran.Window.Enemies();
        EnemiesWindowSectionViewModel raymond = fleet.Raymond.Window.Enemies();
        void Sees(Pilot pilot, string name, int seconds) =>
            pilot.Window.Enemies().RecordCatchUpSighting(pilot.CharacterId, name, T0.AddSeconds(seconds));

        switch (scenario)
        {
            case "abyssal-from-commander" or "abyssal-from-member":
                await memberBus.PublishAsync(new FleetRunGroupAbyssalUpdatedEvent(new RunGroupAbyssalUpdate(FleetOfTwo.FleetId,
                        ActivityKind.Abyssal, FleetOfTwo.GroupCode, 3, "Dark"),
                    scenario == "abyssal-from-commander" ? FleetOfTwo.JithranId : FleetOfTwo.RaymondsSecondToonId), EventTarget.Local);
                break;
            case "rooms-from-member":
                await memberBus.PublishAsync(new FleetRunGroupRoomsEvent(new RunGroupRooms(FleetOfTwo.FleetId, FleetOfTwo.GroupCode,
                    [new RunGroupRoom(T0.AddSeconds(30), null)]), FleetOfTwo.RaymondsSecondToonId), EventTarget.Local);
                break;
            case "member-detects" or "commander-offline":
                Sees(fleet.Raymond, "Striking Damavik", 0);
                Sees(fleet.Raymond, "Starving Vedmak", 30);
                await fleet.SettleAsync(() => raymond.EnemyRooms.Count > 0 && raymond.EnemyRooms[0].Source?.Badge == "FC");
                if (scenario == "commander-offline")
                {
                    fleet.Raymond.Wire.Destinations.Clear();
                    fleet.Jithran.Wire.Destinations.Clear();
                    Sees(fleet.Raymond, "Ephialtes Entangler", 200);
                }

                break;
            case "commander-new-room" or "commander-undo":
                jithran.StartRoom(T0.AddSeconds(90));
                await fleet.SettleAsync(() => raymond.HasRooms);
                if (scenario == "commander-undo")
                {
                    jithran.UndoRoomCommand.Execute(null);
                    await fleet.SettleAsync(() => !raymond.HasRooms);
                }

                break;
        }

        await FleetOfTwo.RunJobsAsync();
        string Rooms(Pilot pilot) => string.Join(",", pilot.Window.Enemies().RoomBoundaries.Select(at => $"+{(at - T0).TotalSeconds}"));
        Assert.Equal(expected, $"J:{Rooms(fleet.Jithran)} R:{Rooms(fleet.Raymond)} "
            + $"{raymond.CurrentRoomSource?.Badge ?? "-"} {(raymond.IsDetecting ? "detecting" : "stopped")} "
            + $"tier{fleet.Raymond.Window.TierIndex?.ToString() ?? "-"}");
    }

    /// <summary>One gate seen from two logs is one room, the commander adopts only what comes after his own last room,
    /// and following the commander regroups this pilot's rows. Counter-proof: drop the 60 s rule and "own-after-adopted"
    /// opens a second room for the same gate.</summary>
    [Theory]
    [InlineData("adopt", "+120|A1|detecting")]
    [InlineData("adopt-same-gate", "+30|A1 B2|detecting")]
    [InlineData("adopt-before-last", "+30|A1 B2|detecting")]
    [InlineData("adopt-fourth", "+120 +300|A1|detecting")]
    [InlineData("own-after-adopted", "+30|A1 B2|detecting")]
    [InlineData("follow", "+150|A1 B2|detecting")]
    [InlineData("follow-hand", "+150|A1 B2|stopped")]
    public void Rooms_FromAnotherLog_FollowTheOneGateRule(string scenario, string expected)
    {
        DateTime start = new(2026, 10, 9, 20, 0, 0, DateTimeKind.Utc);
        var collector = new RunEnemyObservationCollector(1, name => name switch { "A" => 1, "B" => 2, _ => null }, _ => true);
        collector.Record(1, "A", start);
        switch (scenario)
        {
            case "adopt":
                collector.Adopt(start.AddSeconds(120), RoomCertainty.Probable);
                break;
            case "adopt-same-gate":
                collector.Record(1, "B", start.AddSeconds(30));
                collector.Adopt(start.AddSeconds(60), RoomCertainty.Sure);
                break;
            case "adopt-before-last":
                collector.Record(1, "B", start.AddSeconds(30));
                collector.Adopt(start.AddSeconds(10), RoomCertainty.Sure);
                break;
            case "adopt-fourth":
                collector.Adopt(start.AddSeconds(120), RoomCertainty.Probable);
                collector.Adopt(start.AddSeconds(300), RoomCertainty.Probable);
                collector.Adopt(start.AddSeconds(500), RoomCertainty.Probable);
                break;
            case "own-after-adopted":
                collector.Adopt(start.AddSeconds(30), RoomCertainty.Probable);
                collector.Record(1, "B", start.AddSeconds(40));
                break;
            case "follow" or "follow-hand":
                collector.Record(1, "A", start.AddSeconds(100));
                collector.Record(1, "B", start.AddSeconds(200));
                collector.Follow([(start.AddSeconds(150), scenario == "follow" ? RoomCertainty.Probable : null)]);
                break;
        }

        string boundaries = string.Join(" ", collector.RoomBoundaries.Select(at => $"+{(at - start).TotalSeconds}"));
        string rows = string.Join(" ", collector.Observations.Select(row => $"{row.EnemyName}{row.RoomNumber}"));
        Assert.Equal(expected, $"{boundaries}|{rows}|{(collector.IsDetecting ? "detecting" : "stopped")}");
    }
}
