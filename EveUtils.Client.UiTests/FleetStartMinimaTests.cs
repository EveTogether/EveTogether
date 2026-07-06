using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream B / B-5: starting a fleet whose coupled doctrine minimums are not met warns the FC, but does not
/// block — cancelling the warning leaves the fleet Forming, accepting it proceeds (an FC may deliberately start an
/// under-strength pug/roam). Drives the real roster over the local seam with a coupled, under-filled doctrine.
/// </summary>
public class FleetStartMinimaTests
{
    private const int Owner = 95000001;

    private static FleetInfo InfoFor(EveUtils.Shared.Modules.Fleet.Entities.Fleet fleet) =>
        new(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation, fleet.FleetCompositionId);

    [AvaloniaFact]
    public async Task Start_WithUnmetDoctrineMinimums_WarnsAndCancelDoesNotStart()
    {
        var recording = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("FC", Owner));

        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();
        var compositionRepository = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        var compositions = new LocalFleetCompositionClient(fleetService, compositionRepository, Owner);

        var created = await fleetService.CreateLocalFleetAsync("under-strength", null, Owner);
        var fleetId = created.Value;

        // Doctrine wants DPS ≥ 40 — nobody is assigned to it, so the minimum is unmet.
        var composition = await compositions.CreateAsync("Homefront Vanguard", null);
        await compositions.AddRoleAsync(composition.Id, "DPS", 40);
        Assert.True((await client.SetFleetCompositionAsync(fleetId, composition.Id)).Ok);

        var fleet = await repository.GetAsync(fleetId);
        var roster = new FleetRosterViewModel(instance.Services, client, InfoFor(fleet!), isOwner: true, Owner, compositions: compositions);
        for (var i = 0; i < 100 && !roster.CanStart; i++)
            await Task.Delay(50);

        // The FC is warned the doctrine is under-strength and cancels → the fleet stays Forming (not started).
        string? warnedTitle = null;
        recording.OnConfirm = (title, _) =>
        {
            warnedTitle = title;
            return Task.FromResult(false);
        };

        await roster.StartCommand.ExecuteAsync(null);

        Assert.Equal("Start under-strength?", warnedTitle);
        Assert.Equal(FleetActivation.Forming, (await repository.GetAsync(fleetId))!.Activation);
    }
}
