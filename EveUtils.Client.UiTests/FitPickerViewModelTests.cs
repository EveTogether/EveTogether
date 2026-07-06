using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// View-model checks for the reusable fit picker: it lists the local library as selectable rows, builds the
/// <see cref="EveUtils.Client.Fleet.FitReferenceInfo"/> snapshot the composition editor stores, filters by name, and
/// disables a fit already in the target role group so it can't be added twice.
/// </summary>
public class FitPickerViewModelTests
{
    private const int Owner = 95400001;

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static async Task SeedLocalFitAsync(IServiceProvider services, int owner, string name, int shipTypeId, string contentHash) =>
        await services.GetRequiredService<IFittingRepository>().UpsertAsync(new LocalFitting
        {
            OwnerId = owner.ToString(), EsiFittingId = shipTypeId, Name = name, ShipTypeId = shipTypeId,
            RawJson = "{}", ContentHash = contentHash, ImportedAt = DateTimeOffset.UtcNow
        });

    [AvaloniaFact]
    public async Task Load_ListsLocalLibrary_AndBuildsFitReference()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Guardian — Armor", 11987, "hash-guardian");

        var vm = new FitPickerViewModel(instance.Services);
        await vm.EnsureLoadedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Guardian — Armor", row.FitName);
        Assert.Equal("Local", row.Source);

        row.ToggleCommand.Execute(null);
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.CanConfirm);

        var fit = Assert.Single(vm.SelectedFits());
        Assert.Equal(11987, fit.ShipTypeId);
        Assert.Equal("Guardian — Armor", fit.FitName);
        Assert.Equal("hash-guardian", fit.ContentHash);
        Assert.NotNull(fit.LocalFittingId);
        Assert.Null(fit.ServerSharedFitId);
    }

    [AvaloniaFact]
    public async Task Search_FiltersByName()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Guardian — Armor", 11987, "h1");
        await SeedLocalFitAsync(instance.Services, Owner, "Scimitar — Shield", 11978, "h2");

        var vm = new FitPickerViewModel(instance.Services);
        await vm.EnsureLoadedAsync();
        Assert.Equal(2, vm.Rows.Count);

        vm.SearchText = "guardian";
        Assert.Equal("Guardian — Armor", Assert.Single(vm.Rows).FitName);
    }

    [AvaloniaFact]
    public async Task AlreadyAddedFit_IsDisabledAndNotSelectable()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedLocalFitAsync(instance.Services, Owner, "Guardian — Armor", 11987, "in-group");

        var vm = new FitPickerViewModel(instance.Services, ["in-group"]);
        await vm.EnsureLoadedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.True(row.AlreadyAdded);

        row.ToggleCommand.Execute(null);   // a fit already in the group can't be selected
        Assert.False(row.IsSelected);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanConfirm);
    }
}
