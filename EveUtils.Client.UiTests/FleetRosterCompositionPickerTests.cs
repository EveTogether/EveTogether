using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Coupling a composition to a fleet must pick from the whole server-wide library, not just the acting character's
/// own compositions — a doctrine is usually a shared-server one authored by someone else. Regression for the live
/// bug where the picker reported "none available" while a composition existed on the server (it called ListAsync =
/// ListByOwner instead of ListAllAsync).
/// </summary>
public class FleetRosterCompositionPickerTests
{
    private const int Owner = 95000020;

    [AvaloniaFact]
    public async Task ChangeComposition_OffersServerWideLibrary_NotOwnedOnly()
    {
        var dialog = new RecordingDialogService { OnPickCharacter = (_, _) => Task.FromResult<int?>(null) };
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialog));

        var fleetService = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var characters = instance.Services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("picker test", null, Owner);
        var fleet = await repository.GetAsync(created.Value, TestContext.Current.CancellationToken);

        // The acting character owns nothing (ListAsync empty), but the server library holds one composition someone
        // else authored — exactly the live scenario (no local compositions, one on the server).
        var compositions = new RecordingCompositionClient(sharesFitsToServer: true);
        compositions.ServerWideCompositions.Add(new FleetCompositionInfo(
            42, "Shield Doctrine", null, OwnerCharacterId: 90000001, default, default, CanEdit: false, OwnerName: "Someone Else"));

        var info = new FleetInfo(fleet!.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, FleetActivation.Forming);
        using var roster = new FleetRosterViewModel(
            instance.Services, new LocalFleetClient(fleetService, repository, characters, Owner),
            info, isOwner: true, Owner, compositions: compositions);

        await roster.ChangeCompositionCommand.ExecuteAsync(null);

        var option = Assert.Single(dialog.LastOptions!);   // the server-wide composition was offered, not "none available"
        Assert.Equal("Shield Doctrine", option.Name);
        Assert.NotEqual("No compositions to couple — create one in the Compositions library first.", roster.StatusMessage);
    }
}
