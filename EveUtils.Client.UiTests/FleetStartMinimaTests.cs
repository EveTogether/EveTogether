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
///
/// <para><b>Both</b> ways in are covered here, and that pairing is the point. The rule used to be guarded on the
/// roster window alone; ET-168 rebuilt the start flow on both and dropped the warning from both, and only one half
/// of that had a test to notice. A rule that lives in two places needs a test in two places.</para>
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

    /// <summary>
    /// The same rule on the fleet overview's own START (ET-170 put one there). The warning has to stand in front of
    /// the start dialog and not inside it: that dialog scrolls and its START button is pinned to the footer, so a
    /// note below the fold is one an FC presses past without reading. Cancelling the question leaves the fleet
    /// Forming, exactly as on the roster window.
    /// </summary>
    [AvaloniaFact]
    public async Task StartOnTheOverviewRow_WithUnmetDoctrineMinimums_WarnsAndCancelDoesNotStart()
    {
        var (recording, instance, vm, repository, fleetId) = await UnderStrengthOverviewAsync();
        using (instance)
        {
            string? warnedTitle = null;
            recording.OnConfirm = (title, _) =>
            {
                warnedTitle = title;
                return Task.FromResult(false);
            };

            await vm.StartRowCommand.ExecuteAsync(Assert.Single(vm.StandingByFleets));

            Assert.Equal("Start under-strength?", warnedTitle);
            Assert.Equal(FleetActivation.Forming, (await repository.GetAsync(fleetId))!.Activation);
            vm.Dispose();
        }
    }

    /// <summary>It warns; it never blocks. An FC who says "start anyway" gets the ordinary start dialog next and the
    /// fleet runs — the half of B-5 that a plain refusal would quietly break.</summary>
    [AvaloniaFact]
    public async Task StartOnTheOverviewRow_WithUnmetDoctrineMinimums_ProceedsWhenTheFcSaysStartAnyway()
    {
        var (recording, instance, vm, repository, fleetId) = await UnderStrengthOverviewAsync();
        using (instance)
        {
            recording.OnConfirm = (_, _) => Task.FromResult(true);
            recording.FleetStart = FleetStartChoice.LeaveThem;

            await vm.StartRowCommand.ExecuteAsync(Assert.Single(vm.StandingByFleets));

            Assert.Equal("Start under-strength?", recording.LastConfirmTitle);
            Assert.NotNull(recording.FleetStartPrompt);   // the warning came first, the start dialog after it
            Assert.Equal(FleetActivation.Active, (await repository.GetAsync(fleetId))!.Activation);
            vm.Dispose();
        }
    }

    /// <summary>A client-only fleet with a doctrine that wants 40 DPS and nobody assigned to it, on the overview.</summary>
    private static async Task<(RecordingDialogService Dialogs, TestClientInstance Instance, FleetsViewModel Vm,
        IFleetRepository Repository, long FleetId)> UnderStrengthOverviewAsync()
    {
        var recording = new RecordingDialogService();
        var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(recording));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("FC", Owner));

        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var compositionRepository = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var compositions = new LocalFleetCompositionClient(fleetService, compositionRepository, Owner);
        var client = new LocalFleetClient(fleetService, repository,
            instance.Services.GetRequiredService<ICharacterRegistry>(), Owner);

        var fleetId = (await fleetService.CreateLocalFleetAsync("under-strength", null, Owner)).Value;
        var composition = await compositions.CreateAsync("Homefront Vanguard", null);
        await compositions.AddRoleAsync(composition.Id, "DPS", 40);
        Assert.True((await client.SetFleetCompositionAsync(fleetId, composition.Id)).Ok);

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 100 && vm.StandingByFleets.Count == 0; i++)
            await Task.Delay(50);
        Assert.Single(vm.StandingByFleets);
        return (recording, instance, vm, repository, fleetId);
    }
}
