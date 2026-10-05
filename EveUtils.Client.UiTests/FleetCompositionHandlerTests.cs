using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Skills;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Happy-path + validation checks for the composition CQRS handlers, dispatched through the real
/// local pipeline. The v1 access policy (OwnerAllPolicy) allows every actor, so these exercise the create/build/
/// edit/delete flow and the input guards; the owner-or-right denial is covered by
/// <see cref="FleetCompositionAuthorizationTests"/>.
/// </summary>
public class FleetCompositionHandlerTests
{
    private const int Owner = 95200001;

    private static FitReference Fit(string name, int shipTypeId) => new()
    {
        ShipTypeId = shipTypeId, FitName = name, RawJson = "{}", ContentHash = name + shipTypeId
    };

    [AvaloniaFact]
    public async Task Create_PersistsWithOwnerAndTimestamps()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var result = await dispatcher.Send(new CreateFleetCompositionCommand("Shield doctrine", "Tengu fleet", IsClientOnly: true, Owner));

        Assert.True(result.IsSuccess);
        var saved = await repo.GetAsync(result.Value);
        Assert.NotNull(saved);
        Assert.Equal("Shield doctrine", saved!.Name);
        Assert.Equal(Owner, saved.OwnerCharacterId);
        Assert.True(saved.IsClientOnly);
        Assert.NotEqual(default, saved.CreatedAt);
    }

    [AvaloniaFact]
    public async Task Create_BlankName_FailsValidation()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new CreateFleetCompositionCommand("   ", null, false, Owner));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Messages, m => m.Code == MessageCodes.ValidationFailed);
    }

    [AvaloniaFact]
    public async Task AddRole_ThenAddEntry_AppendsInOrderAndReflectsInGraph()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var composition = await dispatcher.Send(new CreateFleetCompositionCommand("Doctrine", null, false, Owner));
        var role = await dispatcher.Send(new AddFleetCompositionRoleCommand(composition.Value, "Logistics", GroupMinCount: 5, Owner));
        Assert.True(role.IsSuccess);

        var firstEntry = await dispatcher.Send(new AddFleetCompositionEntryCommand(role.Value, Fit("Guardian", 11987), EntryMinCount: 3, Owner));
        var secondEntry = await dispatcher.Send(new AddFleetCompositionEntryCommand(role.Value, Fit("Scimitar", 11985), EntryMinCount: null, Owner));
        Assert.True(firstEntry.IsSuccess);
        Assert.True(secondEntry.IsSuccess);

        var graph = await repo.GetGraphAsync(composition.Value);
        var logi = graph!.Roles.Single();
        Assert.Equal(5, logi.Role.GroupMinCount);
        Assert.Equal(new[] { "Guardian", "Scimitar" }, logi.Entries.Select(e => e.Fit.FitName).ToArray());
        Assert.Equal(new[] { 0, 1 }, logi.Entries.Select(e => e.SortOrder).ToArray());
    }

    [AvaloniaFact]
    public async Task AddEntry_EmptyFit_FailsValidation()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        var composition = await dispatcher.Send(new CreateFleetCompositionCommand("Doctrine", null, false, Owner));
        var role = await dispatcher.Send(new AddFleetCompositionRoleCommand(composition.Value, "DPS", null, Owner));

        var result = await dispatcher.Send(new AddFleetCompositionEntryCommand(
            role.Value, new FitReference { FitName = "", ShipTypeId = 0 }, null, Owner));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Messages, m => m.Code == MessageCodes.ValidationFailed);
    }

    [AvaloniaFact]
    public async Task EditRole_SetsGroupMinCount()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var composition = await dispatcher.Send(new CreateFleetCompositionCommand("Doctrine", null, false, Owner));
        var role = await dispatcher.Send(new AddFleetCompositionRoleCommand(composition.Value, "DPS", null, Owner));

        var edit = await dispatcher.Send(new EditFleetCompositionRoleCommand(role.Value, "Heavy DPS", GroupMinCount: 40, Owner));
        Assert.True(edit.IsSuccess);

        var saved = await repo.GetRoleAsync(role.Value);
        Assert.Equal("Heavy DPS", saved!.RoleName);
        Assert.Equal(40, saved.GroupMinCount);
    }

    [AvaloniaFact]
    public async Task Delete_RemovesComposition()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();

        var composition = await dispatcher.Send(new CreateFleetCompositionCommand("Doctrine", null, false, Owner));
        var delete = await dispatcher.Send(new DeleteFleetCompositionCommand(composition.Value, Owner));

        Assert.True(delete.IsSuccess);
        Assert.Null(await repo.GetAsync(composition.Value));
    }

    /// <summary>ET-353 A3: removing an entry takes its skill minimums with it, and only its own.</summary>
    [AvaloniaFact]
    public async Task RemoveEntry_CascadesItsSkillMinimums()
    {
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var composition = await dispatcher.Send(new CreateFleetCompositionCommand("Doctrine", null, true, Owner));
        var role = await dispatcher.Send(new AddFleetCompositionRoleCommand(composition.Value, "Logistics", null, Owner));
        var removed = await dispatcher.Send(new AddFleetCompositionEntryCommand(role.Value, Fit("Guardian", 11987), null, Owner,
            [new SkillMinimum(3336, 5), new SkillMinimum(12096, 4)]));
        var kept = await dispatcher.Send(new AddFleetCompositionEntryCommand(role.Value, Fit("Scimitar", 11985), null, Owner,
            [new SkillMinimum(3336, 4)]));

        var remove = await dispatcher.Send(new RemoveFleetCompositionEntryCommand(removed.Value, Owner));

        Assert.True(remove.IsSuccess);
        await using var db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        var owners = await db.Database
            .SqlQueryRaw<long>("SELECT EntryId AS Value FROM FleetCompositionEntrySkillMinimum").ToListAsync();
        Assert.Equal([kept.Value], owners);
    }

    /// <summary>ET-353 A5: a client-only library stores the minimums in the local database through the Shared handlers
    /// and never reaches for the transport.</summary>
    [AvaloniaFact]
    public async Task LocalLibrary_StoresSkillMinimums_WithoutATransportCall()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IFleetTransportClient>(transport));
        var client = new LocalFleetCompositionClient(instance.Services.GetRequiredService<ClientFleetService>(),
            instance.Services.GetRequiredService<IFleetCompositionRepository>(), Owner);
        var (_, _, compositionId) = await client.CreateAsync("Local doctrine", null);
        var (_, _, roleId) = await client.AddRoleAsync(compositionId, "DPS", null);
        var (_, _, entryId) = await client.AddEntryAsync(roleId,
            new FitReferenceInfo(11987, "Guardian", "{}", "h-guardian", null, null), null, [new SkillMinimum(3336, 4)]);

        var (edited, message) = await client.EditEntryAsync(entryId, null, [new SkillMinimum(3336, 5)]);

        Assert.True(edited, message);
        var detail = await client.GetAsync(compositionId);
        var entry = Assert.Single(Assert.Single(detail?.Roles ?? []).Entries);
        Assert.Equal([new SkillMinimum(3336, 5)], entry.SkillMinimums);
        Assert.Equal(0, transport.CompositionEntryCalls);
    }
}
