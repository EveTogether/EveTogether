using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Skills;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A background ESI poller must skip its whole cycle while ESI is down rather than fire per-character calls the gate
/// would only withhold. SkillRefreshService stands in
/// for the five data pollers that share the guard; its cycle is exercised directly (no hosting/timing).
/// </summary>
public class SkillRefreshAvailabilityTests
{
    [Fact]
    public async Task SkipsTheCycle_WhenEsiIsUnavailable()
    {
        var importer = new CountingSkillImporter();
        var availability = new EsiAvailabilityState();
        availability.Set(EsiAvailability.Maintenance);
        var service = new SkillRefreshService(importer, new OneCharacterRegistry(), availability, NullLogger<SkillRefreshService>.Instance);

        await service.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, importer.Calls); // ESI down → not a single per-character import fired
    }

    [Fact]
    public async Task RunsTheCycle_WhenEsiIsAvailable()
    {
        var importer = new CountingSkillImporter();
        var service = new SkillRefreshService(importer, new OneCharacterRegistry(), new EsiAvailabilityState(), NullLogger<SkillRefreshService>.Instance);

        await service.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, importer.Calls); // available → the character is imported as before
    }

    private sealed class CountingSkillImporter : IEsiSkillImporter
    {
        public int Calls { get; private set; }

        public Task<SkillImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(SkillImportResult.Ok(0));
        }
    }

    private sealed class OneCharacterRegistry : ICharacterRegistry
    {
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Character>>([new Character("Pilot", 1)]);

        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event Action RegistryChanged { add { } remove { } }
    }
}
