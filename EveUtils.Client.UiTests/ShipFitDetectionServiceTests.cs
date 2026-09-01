using EveUtils.Client.Esi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class ShipFitDetectionServiceTests
{
    [Fact]
    public async Task RefreshAllAsync_UnobservedScopeMissingAndNoFitFoundStayDistinct()
    {
        var ships = new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 9, ShipName = "Gila" });
        var service = Build(ships,
        [
            new Character("With scope", 1, ["esi-location.read_ship_type.v1"]),
            new Character("Without scope", 2, []),
        ], []);

        await service.RefreshAllAsync(TestContext.Current.CancellationToken);

        ShipFitDetectionReading unobserved = service.GetReading(3);
        ShipFitDetectionReading noFitFound = service.GetReading(1);
        ShipFitDetectionReading scopeMissing = service.GetReading(2);

        Assert.Equal(ShipFitDetectionState.Unobserved, unobserved.State);
        Assert.Equal(ShipFitDetectionState.Observed, noFitFound.State);
        Assert.Equal(ShipFitMatchReason.NoFitFound, noFitFound.MatchReason);
        Assert.NotNull(noFitFound.ObservedAtUtc);
        Assert.Equal(ShipFitDetectionState.ScopeMissing, scopeMissing.State);
        Assert.NotEqual(unobserved.State, noFitFound.State);
        Assert.NotEqual(scopeMissing.State, noFitFound.State);
        Assert.Equal(1, ships.Calls);
    }

    [Fact]
    public async Task RefreshCharacterAsync_MultipleFitsForTheSameHullReportsAmbiguity()
    {
        var ships = new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 9, ShipName = "Gila" });
        var service = Build(ships, [],
        [
            new LocalFitting { Id = 1, Name = "Abyss Gila", ShipTypeId = 17715 },
            new LocalFitting { Id = 2, Name = "Rat Gila", ShipTypeId = 17715 },
        ]);

        await service.RefreshCharacterAsync(1, TestContext.Current.CancellationToken);

        ShipFitDetectionReading reading = service.GetReading(1);
        Assert.Equal(ShipFitMatchReason.AmbiguousShipType, reading.MatchReason);
        Assert.Null(reading.SelectedFit);
        Assert.Equal(2, reading.Candidates.Count);
    }

    [Fact]
    public async Task SetManualFitAsync_AfterRecreatingServiceWinsAutomaticMatch()
    {
        var settings = new FakeSettingsRepository();
        LocalFitting fit = new() { Id = 7, Name = "Abyss Gila", ShipTypeId = 17715 };
        var first = Build(new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 9, ShipName = "Gila" }), [], [fit], settings);

        await first.SetManualFitAsync(1, fit.Id, TestContext.Current.CancellationToken);

        var second = Build(new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 10, ShipName = "Gila" }),
            [new Character("With scope", 1, ["esi-location.read_ship_type.v1"])], [fit], settings);
        await second.RefreshAllAsync(TestContext.Current.CancellationToken);

        ShipFitDetectionReading reading = second.GetReading(1);
        Assert.Equal(ShipFitMatchReason.Manual, reading.MatchReason);
        Assert.Equal(fit.Id, reading.SelectedFit?.Id);
    }

    [Fact]
    public async Task SetManualFitAsync_AfterChangingHullDoesNotApplyThePreviousHullOverride()
    {
        var settings = new FakeSettingsRepository();
        LocalFitting gila = new() { Id = 7, Name = "Abyss Gila", ShipTypeId = 17715 };
        LocalFitting loki = new() { Id = 8, Name = "Armor Loki", ShipTypeId = 29990 };
        var first = Build(new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 9, ShipName = "Gila" }), [], [gila, loki], settings);

        await first.SetManualFitAsync(1, gila.Id, TestContext.Current.CancellationToken);

        var second = Build(new FakeShipClient(new EsiCharacterShip { ShipTypeId = 29990, ShipItemId = 10, ShipName = "Loki" }),
            [new Character("With scope", 1, ["esi-location.read_ship_type.v1"])], [gila, loki], settings);
        await second.RefreshAllAsync(TestContext.Current.CancellationToken);

        ShipFitDetectionReading reading = second.GetReading(1);
        Assert.Equal(ShipFitMatchReason.OnlyFitForShipType, reading.MatchReason);
        Assert.Equal(loki.Id, reading.SelectedFit?.Id);
    }

    [Fact]
    public async Task RefreshAllAsync_LowBucketHeadroomSkipsTheNonEssentialShipPoll()
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);
        monitor.RecordBucket("app:1", "/characters/{id}/ship/",
            new EsiRateLimitHeaders(99, DateTimeOffset.UtcNow.AddSeconds(60), "character", 150, 5, 145, null), 200);
        var ships = new FakeShipClient(new EsiCharacterShip { ShipTypeId = 17715, ShipItemId = 9, ShipName = "Gila" });
        var service = Build(ships, [new Character("With scope", 1, ["esi-location.read_ship_type.v1"])], [], rateLimits: monitor);

        await service.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, ships.Calls);
        Assert.Equal(ShipFitDetectionState.Unobserved, service.GetReading(1).State);
    }

    private static ShipFitDetectionService Build(FakeShipClient ships, IReadOnlyList<Character> characters,
        IReadOnlyList<LocalFitting> fittings, FakeSettingsRepository? settings = null, IEsiRateLimitMonitor? rateLimits = null) =>
        new(ships, new FakeCharacterRegistry(characters), new FakeFittingRepository(fittings), settings ?? new FakeSettingsRepository(),
            new EsiAvailabilityState(), rateLimits ?? new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance),
            NullLogger<ShipFitDetectionService>.Instance);

    private sealed class FakeShipClient(EsiCharacterShip ship) : IEsiCharacterShipClient
    {
        public int Calls { get; private set; }

        public Task<EsiResult<EsiCharacterShip>> GetShipAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(EsiResult<EsiCharacterShip>.Ok(ship));
        }
    }

    private sealed class FakeCharacterRegistry(IReadOnlyList<Character> characters) : ICharacterRegistry
    {
        public event Action? RegistryChanged { add { } remove { } }
        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(characters);
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeFittingRepository(IReadOnlyList<LocalFitting> fittings) : IFittingRepository
    {
        public Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(fittings);
        public Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalFitting>>(fittings.Where(fitting => fitting.OwnerId == ownerId).ToArray());
        public Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.Id == id));
        public Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.OwnerId == ownerId && fitting.EsiFittingId == esiFittingId));
        public Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(fittings.FirstOrDefault(fitting => fitting.ContentHash == contentHash));
        public Task BackfillContentHashesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSettingsRepository : ISettingRepository
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSetting>>(_values.Select(pair => new ClientSetting { Key = pair.Key, Value = pair.Value }).ToArray());

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
