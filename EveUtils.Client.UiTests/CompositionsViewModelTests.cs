using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// View-model checks for the Fleet Compositions library: the Local tab lists a local character's
/// compositions with their loaded role/fit summary, create adds one, and the search box filters by name. Drives the
/// real local pipeline (client SQLite + shared CQRS) headlessly — no window render needed.
/// </summary>
public class CompositionsViewModelTests
{
    private const int Owner = 95400001;

    private static FitReference Fit(string name, int shipTypeId) => new()
    {
        ShipTypeId = shipTypeId, FitName = name, RawJson = "{}", ContentHash = name + shipTypeId
    };

    private static async Task SeedCharacterAsync(IServiceProvider services, int characterId, string name) =>
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));

    private static async Task<long> SeedCompositionAsync(IServiceProvider services, int owner, string name, int? groupMin, params string[] fits)
    {
        var repo = services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var compositionId = await repo.AddAsync(new FleetComposition { Name = name, OwnerCharacterId = owner, IsClientOnly = true, CreatedAt = now, UpdatedAt = now });
        var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logistics", GroupMinCount = groupMin, SortOrder = 0 });
        var order = 0;
        foreach (var fit in fits)
            await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = roleId, Fit = Fit(fit, 11987 + order), SortOrder = order++ });
        return compositionId;
    }

    [AvaloniaFact]
    public async Task Tabs_LocalLibraryFirst_AndSelected()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();

        var local = Assert.Single(vm.Tabs);   // no coupled servers in the test → just the Local tab
        Assert.Equal("Local library", local.Title);
        Assert.True(local.IsLocal);
        Assert.Same(local, vm.SelectedTab);
    }

    [AvaloniaFact]
    public async Task Reload_ShowsLocalCompositions_WithLoadedSummary()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian", "Scimitar");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();

        var row = Assert.Single(vm.SelectedTab!.Compositions);
        Assert.Equal("Armor doctrine", row.Name);
        Assert.Equal("Pilot One", row.OwnerName);
        Assert.True(row.CanEdit);
        Assert.Equal(1, row.RoleCount);
        Assert.Equal(2, row.FitCount);
        Assert.Equal(5, row.MinPilots);
        Assert.Equal("≥5", Assert.Single(row.RoleChips).MinLabel);
    }

    [AvaloniaFact]
    public async Task CreateLocalComposition_AddsAndShowsIt()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        Assert.Empty(vm.SelectedTab!.Compositions);

        var created = await vm.CreateLocalCompositionAsync("Shield doctrine", Owner);

        Assert.True(created);
        Assert.Equal("Shield doctrine", Assert.Single(vm.SelectedTab!.Compositions).Name);
    }

    [AvaloniaFact]
    public async Task DuplicateComposition_CreatesUniqueCopy_KeepingTheOriginal()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian", "Scimitar");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        var row = Assert.Single(vm.SelectedTab!.Compositions);

        await vm.DuplicateCompositionCommand.ExecuteAsync(row);

        Assert.Contains(vm.SelectedTab!.Compositions, r => r.Name == "Armor doctrine");          // original kept
        var copy = vm.SelectedTab!.Compositions.Single(r => r.Name == "Armor doctrine (copy)");
        await copy.LoadSummaryAsync();
        Assert.Equal(2, copy.FitCount);   // the whole graph was copied, not just the header

        // Duplicating the original again avoids the name collision with a numbered copy.
        await vm.DuplicateCompositionCommand.ExecuteAsync(vm.SelectedTab!.Compositions.First(r => r.Name == "Armor doctrine"));
        Assert.Contains(vm.SelectedTab!.Compositions, r => r.Name == "Armor doctrine (copy 2)");
    }

    [AvaloniaFact]
    public async Task Search_FiltersByName()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", 5, "Guardian");
        await SeedCompositionAsync(instance.Services, Owner, "Shield brawl", 10, "Basilisk");

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        Assert.Equal(2, vm.SelectedTab!.Compositions.Count);

        vm.SearchText = "armor";
        Assert.Equal("Armor doctrine", Assert.Single(vm.SelectedTab!.Compositions).Name);

        vm.SearchText = "";
        Assert.Equal(2, vm.SelectedTab!.Compositions.Count);
    }

    [AvaloniaFact]
    public async Task LoadSummary_PopulatesDistinctHullThumbnails()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");

        // Two roles with one hull (641) repeated across them → the card shows each hull once, in first-seen order.
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var compositionId = await repo.AddAsync(new FleetComposition { Name = "Mixed", OwnerCharacterId = Owner, IsClientOnly = true, CreatedAt = now, UpdatedAt = now });
        var role1 = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "DPS", GroupMinCount = 10, SortOrder = 0 });
        var role2 = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logi", GroupMinCount = 2, SortOrder = 1 });
        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = role1, Fit = Fit("Megathron", 641), SortOrder = 0 });
        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = role1, Fit = Fit("Hyperion", 24690), SortOrder = 1 });
        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = role2, Fit = Fit("Megathron alt", 641), SortOrder = 0 });   // duplicate hull
        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = role2, Fit = Fit("Guardian", 11987), SortOrder = 1 });

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();   // loads each card's summary, which builds the hull thumbnails
        var row = Assert.Single(vm.SelectedTab!.Compositions);

        Assert.Equal(new[] { 641, 24690, 11987 }, row.Hulls.Select(h => h.ShipTypeId).ToArray());
    }

    [AvaloniaFact]
    public async Task Reload_ShowsCoupledFleetCount_PerComposition()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var armor = await SeedCompositionAsync(instance.Services, Owner, "Armor doctrine", groupMin: 5, "Guardian", "Scimitar");
        var shield = await SeedCompositionAsync(instance.Services, Owner, "Shield brawl", groupMin: 10, "Basilisk");
        await SeedCompositionAsync(instance.Services, Owner, "Unused doctrine", groupMin: 1, "Rifter");

        // Couple two fleets to the armor doctrine, one to the shield doctrine, and leave one fleet uncoupled.
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var armorFleet1 = await service.CreateLocalFleetAsync("Armor roam", null, Owner);
        var armorFleet2 = await service.CreateLocalFleetAsync("Armor CTA", null, Owner);
        var shieldFleet = await service.CreateLocalFleetAsync("Shield gank", null, Owner);
        await service.CreateLocalFleetAsync("Freeform", null, Owner);   // uncoupled — must count for nothing
        await service.SetFleetCompositionAsync(armorFleet1.Value, armor, Owner);
        await service.SetFleetCompositionAsync(armorFleet2.Value, armor, Owner);
        await service.SetFleetCompositionAsync(shieldFleet.Value, shield, Owner);

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();

        var rows = vm.SelectedTab!.Compositions;
        var armorRow = rows.Single(r => r.Name == "Armor doctrine");
        var shieldRow = rows.Single(r => r.Name == "Shield brawl");
        var unusedRow = rows.Single(r => r.Name == "Unused doctrine");

        Assert.Equal(2, armorRow.FleetCount);
        Assert.Equal("⛴ 2 fleets", armorRow.FleetCountLabel);   // plural
        Assert.Equal(1, shieldRow.FleetCount);
        Assert.Equal("⛴ 1 fleet", shieldRow.FleetCountLabel);   // singular
        Assert.Equal(0, unusedRow.FleetCount);
        Assert.False(unusedRow.HasFleets);                       // pill hidden when no fleet uses the doctrine
    }

    [AvaloniaFact]
    public async Task CompositionsWindow_Renders()
    {
        using var instance = TestClientInstance.Create();
        await SeedCharacterAsync(instance.Services, Owner, "Pilot One");
        var vanguard = await SeedCompositionAsync(instance.Services, Owner, "Homefront Vanguard", groupMin: 40, "Megathron", "Hyperion");
        await SeedCompositionAsync(instance.Services, Owner, "Wormhole Brawl", groupMin: null, "Guardian", "Scimitar");

        // Couple two fleets to the first doctrine so the "N fleets" pill renders.
        var service = instance.Services.GetRequiredService<ClientFleetService>();
        var fleet1 = await service.CreateLocalFleetAsync("Vanguard A", null, Owner);
        var fleet2 = await service.CreateLocalFleetAsync("Vanguard B", null, Owner);
        await service.SetFleetCompositionAsync(fleet1.Value, vanguard, Owner);
        await service.SetFleetCompositionAsync(fleet2.Value, vanguard, Owner);

        var vm = new CompositionsViewModel(instance.Services);
        await vm.ReloadAsync();
        Assert.Equal(2, vm.SelectedTab!.Compositions.Count);

        var window = new CompositionsWindow(vm) { Width = 780, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-compositions.png");
        window.Close();
    }
}
