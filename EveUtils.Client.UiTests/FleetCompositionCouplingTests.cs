using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Coupling a reusable composition to a fleet: the owner couples/unlinks a doctrine while the fleet is
/// forming, it persists on the fleet and shows in the roster band, and the coupled doctrine then scopes the member
/// fit picker.
/// </summary>
public class FleetCompositionCouplingTests
{
    private const int Owner = 95000001;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation, fleet.FleetCompositionId);

    private static async Task WaitForTreeAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Tree.Count == 0; i++)
            await Task.Delay(50);
    }

    private static async Task<long> SeedDoctrineAsync(LocalFleetCompositionClient client)
    {
        var (_, _, compositionId) = await client.CreateAsync("Homefront Vanguard", "armor doctrine");
        var (_, _, roleId) = await client.AddRoleAsync(compositionId, "Logistics", 5);
        await client.AddEntryAsync(roleId, new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null), 3);
        return compositionId;
    }

    [AvaloniaFact]
    public async Task CoupleAndUnlink_PersistsOnFleet_AndShowsInBand()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var compositionRepo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var created = await service.CreateLocalFleetAsync("Couple test", null, Owner);
        var compositions = new LocalFleetCompositionClient(service, compositionRepo, Owner);
        var compositionId = await SeedDoctrineAsync(compositions);

        var fleetClient = new LocalFleetClient(service, repository, characters, Owner);
        var roster = new FleetRosterViewModel(instance.Services, fleetClient, InfoFor((await repository.GetAsync(created.Value))!),
            isOwner: true, Owner, onActivationChanged: null, compositions: compositions);
        await WaitForTreeAsync(roster);
        Assert.False(roster.HasCoupledComposition);
        Assert.True(roster.CanCoupleComposition);   // owner + forming

        recording.OnPickCharacter = (_, _) => Task.FromResult<int?>((int)compositionId);   // the picker returns the doctrine id
        await roster.ChangeCompositionCommand.ExecuteAsync(null);

        Assert.True(roster.HasCoupledComposition);
        Assert.Equal("Homefront Vanguard", roster.CoupledCompositionName);
        Assert.Equal(compositionId, (await repository.GetAsync(created.Value))!.FleetCompositionId);

        await roster.UnlinkCompositionCommand.ExecuteAsync(null);
        Assert.False(roster.HasCoupledComposition);
        Assert.Null((await repository.GetAsync(created.Value))!.FleetCompositionId);
    }

    [AvaloniaFact]
    public async Task FleetRosterWindow_WithCoupledComposition_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var compositionRepo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var created = await service.CreateLocalFleetAsync("Render test", null, Owner);
        var compositions = new LocalFleetCompositionClient(service, compositionRepo, Owner);
        var compositionId = await SeedDoctrineAsync(compositions);
        await service.SetFleetCompositionAsync(created.Value, compositionId, Owner);

        var fleetClient = new LocalFleetClient(service, repository, characters, Owner);
        var roster = new FleetRosterViewModel(instance.Services, fleetClient, InfoFor((await repository.GetAsync(created.Value))!),
            isOwner: true, Owner, onActivationChanged: null, compositions: compositions);
        await WaitForTreeAsync(roster);

        var window = new FleetRosterWindow(roster) { Width = 760, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-roster-composition.png");
        window.Close();
    }
}
