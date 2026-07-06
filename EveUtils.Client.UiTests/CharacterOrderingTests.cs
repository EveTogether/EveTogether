using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The character list keeps a user-defined order that survives a restart and flows to every consumer (panel, metrics,
/// pickers) via <see cref="ICharacterRegistry.GetAllAsync"/>. Drives the real EF registry over the throwaway client DB.
/// </summary>
public class CharacterOrderingTests
{
    [AvaloniaFact]
    public async Task NewCharacters_AppendInRegistrationOrder()
    {
        using var instance = TestClientInstance.Create();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();

        await registry.AddOrUpdateAsync(new Character("Alpha", 100));
        await registry.AddOrUpdateAsync(new Character("Bravo", 200));
        await registry.AddOrUpdateAsync(new Character("Charlie", 300));

        Assert.Equal(["Alpha", "Bravo", "Charlie"], (await registry.GetAllAsync()).Select(c => c.Name));
    }

    [AvaloniaFact]
    public async Task Reorder_PersistsTheNewOrder()
    {
        using var instance = TestClientInstance.Create();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Alpha", 100));
        await registry.AddOrUpdateAsync(new Character("Bravo", 200));
        await registry.AddOrUpdateAsync(new Character("Charlie", 300));

        await registry.ReorderAsync([300, 100, 200]);

        // A freshly resolved registry reads the same DB → proves the order survives a restart, not just an in-memory move.
        var reloaded = instance.Services.GetRequiredService<ICharacterRegistry>();
        Assert.Equal([300, 100, 200], (await reloaded.GetAllAsync()).Select(c => c.EsiCharacterId));
    }

    [AvaloniaFact]
    public async Task AddingAnExistingCharacter_KeepsItsPosition()
    {
        using var instance = TestClientInstance.Create();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Alpha", 100));
        await registry.AddOrUpdateAsync(new Character("Bravo", 200));
        await registry.ReorderAsync([200, 100]); // Bravo first

        // Re-adding Alpha (e.g. a scope/affiliation refresh) must not jump it back to the top.
        await registry.AddOrUpdateAsync(new Character("Alpha", 100, ["esi-fittings.read_fittings.v1"]));

        Assert.Equal([200, 100], (await registry.GetAllAsync()).Select(c => c.EsiCharacterId));
    }

    [AvaloniaFact]
    public async Task Reorder_WithUnlistedCharacter_KeepsItAfterTheListedOnes()
    {
        using var instance = TestClientInstance.Create();
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Alpha", 100));
        await registry.AddOrUpdateAsync(new Character("Bravo", 200));
        await registry.AddOrUpdateAsync(new Character("Charlie", 300));

        await registry.ReorderAsync([300, 100]); // Charlie, Alpha; Bravo left unlisted

        var order = (await registry.GetAllAsync()).Select(c => c.EsiCharacterId).ToList();
        Assert.Equal([300, 100], order.Take(2));
        Assert.Equal(200, order.Last()); // unlisted Bravo lands after the listed ones
    }
}
