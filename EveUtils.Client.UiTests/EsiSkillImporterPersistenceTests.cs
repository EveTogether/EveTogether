using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Skills;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Beyond the effective levels, a skill import also persists the raw training queue and the character's
/// training attributes for the read-only queue view and the SP/min rate. Runs the real client DI on a
/// throwaway instance with a fake ESI client answering /skills/, /skillqueue/ and /attributes/.
/// </summary>
public class EsiSkillImporterPersistenceTests
{
    [Fact]
    public async Task ImportAsync_PersistsQueueAndAttributes()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/skills/"] = new EsiCharacterSkills
        {
            Skills = [new EsiSkill { SkillId = 3300, TrainedSkillLevel = 4 }]
        };
        esi.Responses["/characters/77/skillqueue/"] = new[]
        {
            new EsiSkillQueueEntry { SkillId = 3300, FinishedLevel = 5, QueuePosition = 0,
                StartDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                FinishDate = new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero) },
            new EsiSkillQueueEntry { SkillId = 3301, FinishedLevel = 3, QueuePosition = 1 },
        };
        esi.Responses["/characters/77/attributes/"] = new EsiCharacterAttributes
        {
            Charisma = 19, Intelligence = 20, Memory = 21, Perception = 22, Willpower = 23
        };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));

        var result = await instance.Services.GetRequiredService<IEsiSkillImporter>()
            .ImportAsync(77, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);

        var queue = await instance.Services.GetRequiredService<ICharacterSkillQueueRepository>()
            .GetForCharacterAsync(77, TestContext.Current.CancellationToken);
        Assert.Equal(2, queue.Count);
        Assert.Equal(0, queue[0].QueuePosition);            // ordered by position, currently-training first
        Assert.Equal(3300, queue[0].SkillTypeId);
        Assert.Equal(5, queue[0].FinishedLevel);
        Assert.NotNull(queue[0].FinishDate);

        var attributes = await instance.Services.GetRequiredService<ICharacterAttributesRepository>()
            .GetAsync(77, TestContext.Current.CancellationToken);
        Assert.NotNull(attributes);
        Assert.Equal(22, attributes!.Perception);
        Assert.Equal(20, attributes.Intelligence);
    }

    [Fact]
    public async Task ImportAsync_ConcurrentForSameCharacter_SerialisesPerCharacter()
    {
        // Regression: the background refresh (timer + the RegistryChanged fire-and-forget) and the on-demand fit-detail
        // import can call ImportAsync for the same character concurrently. Each ReplaceForCharacterAsync deletes-then-
        // inserts, so overlapping writes race into "UNIQUE constraint failed: CharacterSkill.CharacterId,
        // CharacterSkill.SkillTypeId" against the real SQLite store. The importer must serialise per character — this
        // fake records the peak overlap inside the write (deterministic, unlike timing a real DB race).
        const int characterId = 7777;
        var esi = new RoutingEsiClient();
        esi.Responses[$"/characters/{characterId}/skills/"] = new EsiCharacterSkills
        {
            Skills = [new EsiSkill { SkillId = 3300, TrainedSkillLevel = 4 }]
        };
        esi.Responses[$"/characters/{characterId}/skillqueue/"] = Array.Empty<EsiSkillQueueEntry>();
        esi.Responses[$"/characters/{characterId}/attributes/"] = new EsiCharacterAttributes
        {
            Charisma = 1, Intelligence = 1, Memory = 1, Perception = 1, Willpower = 1
        };
        var skillRepository = new ConcurrencyTrackingSkillRepository();
        var importer = new EsiSkillImporter(esi, skillRepository, new NoOpQueueRepository(), new NoOpAttributesRepository());

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => importer.ImportAsync(characterId, TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(6, skillRepository.Calls);
        Assert.Equal(1, skillRepository.MaxConcurrent);   // never two writes for one character at once
    }

    /// <summary>Records the peak number of overlapping writes so a test can assert per-character serialisation.</summary>
    private sealed class ConcurrencyTrackingSkillRepository : ICharacterSkillRepository
    {
        private readonly object _gate = new();
        private int _current;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        public async Task ReplaceForCharacterAsync(int characterId, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken = default)
        {
            var depth = Interlocked.Increment(ref _current);
            lock (_gate)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, depth);
                Calls++;
            }
            await Task.Delay(25, cancellationToken);   // hold the critical section so any overlap is observable
            Interlocked.Decrement(ref _current);
        }

        public Task<IReadOnlyDictionary<int, int>> GetLevelsAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyDictionary<int, int>)new Dictionary<int, int>());

        public Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoOpQueueRepository : ICharacterSkillQueueRepository
    {
        public Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<CharacterSkillQueueEntry> entries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CharacterSkillQueueEntry>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<CharacterSkillQueueEntry>)[]);
    }

    private sealed class NoOpAttributesRepository : ICharacterAttributesRepository
    {
        public Task ReplaceForCharacterAsync(CharacterAttributes attributes, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CharacterAttributes?> GetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterAttributes?>(null);
    }

    /// <summary>An <see cref="IEsiClient"/> that answers each typed GET from a per-path response table.</summary>
    private sealed class RoutingEsiClient : IEsiClient
    {
        public Dictionary<string, object?> Responses { get; } = new();

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Responses.TryGetValue(request.Path, out var value) && value is T typed
                ? EsiResult<T>.Ok(typed)
                : EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, $"no stub for {request.Path}", 500)));
    }
}
