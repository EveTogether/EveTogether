using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Coupling a composition to a fleet is pushed to its members as fleet.changed/CompositionChanged, so a
/// viewer's open roster shows the coupled-doctrine band live — not just the owner who changed it locally. Drives the
/// real <see cref="FleetRosterViewModel"/> over a client-only fleet: the band starts empty, a composition is coupled
/// out-of-band (as another client would), and the matching event makes the band resolve the doctrine without reopening.
/// </summary>
public class FleetRosterCompositionLiveRefreshTests
{
    private const int Owner = 95000010;

    [AvaloniaFact]
    public async Task Roster_ShowsCoupledComposition_OnCompositionChangedEvent()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();
        var compositionRepository = services.GetRequiredService<IFleetCompositionRepository>();
        var bus = services.GetRequiredService<IEventBus>();
        var ct = TestContext.Current.CancellationToken;

        var created = await fleetService.CreateLocalFleetAsync("composition refresh", null, Owner);
        var fleetId = created.Value;
        var fleet = await repository.GetAsync(fleetId, ct);
        var info = new FleetInfo(
            fleet!.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        var compositions = new LocalFleetCompositionClient(fleetService, compositionRepository, Owner);
        using var roster = new FleetRosterViewModel(services, client, info, isOwner: true, Owner, compositions: compositions);

        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++) await Task.Delay(50, ct);
        Assert.False(roster.HasCoupledComposition); // no doctrine coupled yet

        // Another client (here: out-of-band on the same store) couples a doctrine to the fleet while it forms.
        var composition = await compositions.CreateAsync("Shield Doctrine", null);
        var coupled = await client.SetFleetCompositionAsync(fleetId, composition.Id);
        Assert.True(coupled.Ok);

        // The matching event makes the open roster pick up the coupling live (the band reads server truth on reload).
        await bus.PublishAsync(new FleetChangedEvent(new FleetChangePayload(fleetId, FleetChangeKind.CompositionChanged)), EventTarget.Local, ct);
        for (var i = 0; i < 100 && !roster.HasCoupledComposition; i++) await Task.Delay(50, ct);

        Assert.True(roster.HasCoupledComposition);
        Assert.Equal("Shield Doctrine", roster.CoupledCompositionName);
    }
}
