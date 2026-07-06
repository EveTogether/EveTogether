using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// An open roster window must refresh live when the fleet's membership changes elsewhere (a join is pushed as
/// fleet.changed) — without it the roster only updated on local actions or a reopen, so other members' joins were
/// invisible until a restart. Drives the real <see cref="FleetRosterViewModel"/> over a client-only fleet and asserts
/// it reloads on a matching event and ignores a non-matching one.
/// </summary>
public class FleetRosterLiveRefreshTests
{
    private const int Owner = 95000001;
    private const int Other = 95000002;

    [AvaloniaFact]
    public async Task Roster_ReloadsLive_OnMatchingFleetChangedEvent_AndIgnoresOtherFleets()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();
        var bus = services.GetRequiredService<IEventBus>();
        var ct = TestContext.Current.CancellationToken;

        var created = await fleetService.CreateLocalFleetAsync("live refresh", null, Owner);
        var fleetId = created.Value;
        var fleet = await repository.GetAsync(fleetId, ct);
        var info = new FleetInfo(
            fleet!.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);
        using var roster = new FleetRosterViewModel(
            services, new LocalFleetClient(fleetService, repository, characters, Owner), info, isOwner: true, Owner);

        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++) await Task.Delay(50, ct);
        Assert.StartsWith("Fleet (1)", RootLabel(roster));

        // A second member joins after the roster was loaded.
        await fleetService.AddLocalCharacterAsync(fleetId, Other, Owner);

        // An unrelated fleet's change must NOT reload this roster.
        await bus.PublishAsync(new FleetChangedEvent(new FleetChangePayload(fleetId + 999, FleetChangeKind.RosterChanged)), EventTarget.Local, ct);
        await Task.Delay(150, ct);
        Assert.StartsWith("Fleet (1)", RootLabel(roster)); // still stale — the event was for another fleet

        // The matching change reloads the open roster live → the new member appears without reopening.
        await bus.PublishAsync(new FleetChangedEvent(new FleetChangePayload(fleetId, FleetChangeKind.RosterChanged)), EventTarget.Local, ct);
        for (var i = 0; i < 100 && !RootLabel(roster).StartsWith("Fleet (2)"); i++) await Task.Delay(50, ct);
        Assert.StartsWith("Fleet (2)", RootLabel(roster));
    }

    private static string RootLabel(FleetRosterViewModel roster) =>
        roster.Tree.OfType<FleetRootNodeViewModel>().Single().Label;
}
