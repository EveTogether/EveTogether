using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-494: in a fleet abyssal the commander's rooms, tier and weather are the fleet's, and only the commander's
/// count — the sender is the one the server attached.</summary>
public class FleetRoomSyncTests
{
    [AvaloniaTheory]
    [InlineData("abyssal", FleetOfTwo.JithranId, true)]
    [InlineData("abyssal", FleetOfTwo.RaymondsSecondToonId, false)]
    public async Task CommanderOnlyEvent_ReachesTheMember_OnlyFromTheCommander(string kind, int sender, bool applied)
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Abyssal);
        IEventBus memberBus = fleet.Raymond.Instance.Services.GetRequiredService<IEventBus>();

        await memberBus.PublishAsync(_Event(kind, sender), EventTarget.Local);
        await FleetOfTwo.RunJobsAsync();

        Assert.Equal(applied, fleet.Raymond.Window.TierIndex == 3);
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

    private static IIntegrationEvent _Event(string kind, int sender) => kind switch
    {
        _ => new FleetRunGroupAbyssalUpdatedEvent(
            new RunGroupAbyssalUpdate(FleetOfTwo.FleetId, ActivityKind.Abyssal, FleetOfTwo.GroupCode, 3, "Dark"), sender)
    };
}
