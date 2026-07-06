using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless UI verification for the client-only roster + DISBAND. Drives the real client DI fully in-process
/// (no server, no gRPC): a client-only fleet is created through the Shared CQRS handlers, opened in the actual
/// <see cref="FleetRosterViewModel"/> + <see cref="FleetRosterWindow"/> the "Manage local" button uses, and the
/// roster tree + a rendered screenshot are asserted. Then DISBAND is exercised and the soft-delete is verified.
/// </summary>
public class ClientOnlyFleetRosterTests
{
    private const int Owner = 95000001; // a synthetic local toon owning the fleet (no real ESI identity needed)

    [AvaloniaFact]
    public async Task ManageLocal_Roster_ShowsDefaultTree_ThenDisbandArchives()
    {
        // real client DI on an isolated throwaway instance.
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("UI roster test", null, Owner);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;

        var fleet = await repository.GetAsync(fleetId);
        Assert.NotNull(fleet);
        Assert.True(fleet!.IsClientOnly);

        // open the roster exactly like FleetsViewModel.ManageLocal does, then load it.
        var info = new FleetInfo(
            fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation);
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        var roster = new FleetRosterViewModel(services, client, info, isOwner: true, Owner);

        // The view model loads itself once on construction (fire-and-forget). Wait for that single load to settle —
        // do NOT trigger a second concurrent load (RefreshCommand), which would race the name-resolution cache.
        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
            await Task.Delay(50);

        // the tree binds the client-only fleet: a single Fleet root (1 member = the FC owner) carrying the
        // default Wing 1 → Squad 1 that CreateLocalFleet seeds.
        var root = Assert.IsType<FleetRootNodeViewModel>(Assert.Single(roster.Tree));
        Assert.StartsWith("Fleet (1)", root.Label);
        Assert.Contains("FC:", root.Label);
        var wing = Assert.Single(root.Children.OfType<WingNodeViewModel>());
        Assert.StartsWith("Wing 1", wing.Label);
        var squad = Assert.Single(wing.Children.OfType<SquadNodeViewModel>());
        Assert.StartsWith("Squad 1", squad.Label);

        // Visual proof — render the real roster window and capture real Skia pixels for inspection.
        var window = new FleetRosterWindow(roster) { Width = 900, Height = 600 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster.png");

        // DISBAND (the Local-tab DISBAND button path).
        var disbanded = await fleetService.DisbandFleetAsync(fleetId, Owner);
        Assert.True(disbanded.IsSuccess);

        // the client-only fleet is soft-deleted (Archived).
        var after = await repository.GetAsync(fleetId);
        Assert.NotNull(after);
        Assert.Equal(FleetState.Archived, after!.State);
    }
}
