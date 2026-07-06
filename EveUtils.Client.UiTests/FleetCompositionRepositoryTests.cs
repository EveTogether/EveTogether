using Avalonia.Headless.XUnit;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Persistence checks for the fleet-composition repository against the real migrated client SQLite:
/// the role/entry graph loads in sort order with the owned fit snapshot, reordering rewrites sort order, deleting a
/// composition cascades its roles and entries, and the owner list is scoped.
/// </summary>
public class FleetCompositionRepositoryTests
{
    private const int Owner = 95100001;
    private const int OtherOwner = 95100002;

    private static FitReference Fit(string name, int shipTypeId) => new()
    {
        ShipTypeId = shipTypeId,
        FitName = name,
        RawJson = "{\"ship_type_id\":" + shipTypeId + "}",
        ContentHash = name + ":" + shipTypeId
    };

    private static FleetComposition NewComposition(int owner, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new FleetComposition { Name = name, OwnerCharacterId = owner, CreatedAt = now, UpdatedAt = now };
    }

    [AvaloniaFact]
    public async Task GetGraph_ReturnsRolesAndEntriesInSortOrder_WithOwnedFitSnapshot()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var compositionId = await repo.AddAsync(NewComposition(Owner, "Armor doctrine"));
        var dpsRoleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "DPS", SortOrder = 0 });
        var logiRoleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "Logistics", GroupMinCount = 5, SortOrder = 1 });

        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = logiRoleId, Fit = Fit("Guardian", 11987), EntryMinCount = 3, SortOrder = 0 });
        await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = logiRoleId, Fit = Fit("Scimitar", 11985), EntryMinCount = 2, SortOrder = 1 });

        var graph = await repo.GetGraphAsync(compositionId);

        Assert.NotNull(graph);
        Assert.Equal("Armor doctrine", graph!.Composition.Name);
        Assert.Equal(new[] { "DPS", "Logistics" }, graph.Roles.Select(r => r.Role.RoleName).ToArray());

        var logi = graph.Roles.Single(r => r.Role.Id == logiRoleId);
        Assert.Equal(5, logi.Role.GroupMinCount);
        Assert.Equal(new[] { "Guardian", "Scimitar" }, logi.Entries.Select(e => e.Fit.FitName).ToArray());
        Assert.Equal(3, logi.Entries[0].EntryMinCount);
        // The owned snapshot loads with the entry.
        Assert.Equal(11987, logi.Entries[0].Fit.ShipTypeId);
        Assert.Contains("ship_type_id", logi.Entries[0].Fit.RawJson);

        var dps = graph.Roles.Single(r => r.Role.Id == dpsRoleId);
        Assert.Null(dps.Role.GroupMinCount);
        Assert.Empty(dps.Entries);
    }

    [AvaloniaFact]
    public async Task ReorderRolesAndEntries_RewritesSortOrder()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var compositionId = await repo.AddAsync(NewComposition(Owner, "Doctrine"));
        var roleA = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "A", SortOrder = 0 });
        var roleB = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "B", SortOrder = 1 });
        var entry1 = await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = roleA, Fit = Fit("One", 1), SortOrder = 0 });
        var entry2 = await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = roleA, Fit = Fit("Two", 2), SortOrder = 1 });

        await repo.ReorderRolesAsync(compositionId, [roleB, roleA]);
        await repo.ReorderEntriesAsync(roleA, [entry2, entry1]);

        var graph = await repo.GetGraphAsync(compositionId);
        Assert.Equal(new[] { "B", "A" }, graph!.Roles.Select(r => r.Role.RoleName).ToArray());
        Assert.Equal(new[] { "Two", "One" }, graph.Roles.Single(r => r.Role.Id == roleA).Entries.Select(e => e.Fit.FitName).ToArray());
    }

    [AvaloniaFact]
    public async Task DeleteComposition_CascadesRolesAndEntries()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var compositionId = await repo.AddAsync(NewComposition(Owner, "Throwaway"));
        var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "DPS", SortOrder = 0 });
        var entryId = await repo.AddEntryAsync(new FleetCompositionEntry { RoleId = roleId, Fit = Fit("Megathron", 641), SortOrder = 0 });

        await repo.DeleteAsync(compositionId);

        Assert.Null(await repo.GetAsync(compositionId));
        Assert.Empty(await repo.ListRolesAsync(compositionId));
        Assert.Null(await repo.GetEntryAsync(entryId));
    }

    [AvaloniaFact]
    public async Task ListByOwner_ReturnsOnlyThatOwnersCompositions()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        await repo.AddAsync(NewComposition(Owner, "Mine 1"));
        await repo.AddAsync(NewComposition(Owner, "Mine 2"));
        await repo.AddAsync(NewComposition(OtherOwner, "Theirs"));

        var mine = await repo.ListByOwnerAsync(Owner);
        Assert.Equal(2, mine.Count);
        Assert.All(mine, c => Assert.Equal(Owner, c.OwnerCharacterId));
    }

    [AvaloniaFact]
    public async Task ListAll_ReturnsEveryOwnersCompositions()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        await repo.AddAsync(NewComposition(Owner, "Mine"));
        await repo.AddAsync(NewComposition(OtherOwner, "Theirs"));

        var all = await repo.ListAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.OwnerCharacterId == Owner && c.Name == "Mine");
        Assert.Contains(all, c => c.OwnerCharacterId == OtherOwner && c.Name == "Theirs");
    }
}
