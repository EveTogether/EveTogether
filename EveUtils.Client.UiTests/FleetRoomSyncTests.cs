using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
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

    private static IIntegrationEvent _Event(string kind, int sender) => kind switch
    {
        _ => new FleetRunGroupAbyssalUpdatedEvent(
            new RunGroupAbyssalUpdate(FleetOfTwo.FleetId, ActivityKind.Abyssal, FleetOfTwo.GroupCode, 3, "Dark"), sender)
    };
}
