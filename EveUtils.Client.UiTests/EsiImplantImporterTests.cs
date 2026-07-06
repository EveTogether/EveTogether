using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Implants;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Implants.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Importing a character's implants from ESI: the flat <c>int[]</c> the <c>/implants/</c> endpoint returns is
/// stored as the character's implant set, a re-import replaces it, and an empty list is a valid (no-implant) result.
/// Runs the real client DI on a throwaway instance with a fake ESI client (feedback_test_setup_isolation).
/// </summary>
public class EsiImplantImporterTests
{
    [Fact]
    public async Task ImportAsync_StoresReturnedImplantTypeIds()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/implants/"] = new[] { 10, 20, 30 };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));

        var result = await instance.Services.GetRequiredService<IEsiImplantImporter>()
            .ImportAsync(77, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.ImplantCount);
        var stored = await instance.Services.GetRequiredService<ICharacterImplantRepository>()
            .GetTypeIdsAsync(77, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 10, 20, 30 }, stored.OrderBy(id => id));
    }

    [Fact]
    public async Task ImportAsync_Reimport_ReplacesTheWholeSet()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/implants/"] = new[] { 10, 20 };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));
        var importer = instance.Services.GetRequiredService<IEsiImplantImporter>();
        var repository = instance.Services.GetRequiredService<ICharacterImplantRepository>();

        await importer.ImportAsync(77, TestContext.Current.CancellationToken);
        esi.Responses["/characters/77/implants/"] = new[] { 30 }; // swapped clone since last import
        await importer.ImportAsync(77, TestContext.Current.CancellationToken);

        var stored = await repository.GetTypeIdsAsync(77, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 30 }, stored);
    }

    [Fact]
    public async Task ImportAsync_NoImplants_SucceedsWithEmptySet()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/implants/"] = System.Array.Empty<int>();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));

        var result = await instance.Services.GetRequiredService<IEsiImplantImporter>()
            .ImportAsync(77, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ImplantCount);
        Assert.False(await instance.Services.GetRequiredService<ICharacterImplantRepository>()
            .HasAnyAsync(77, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportAsync_RaisesImplantsChanged_SoTheOverviewBadgeRefreshesLive()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/implants/"] = new[] { 10, 20, 30 };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IEsiClient>(esi));
        var importer = instance.Services.GetRequiredService<IEsiImplantImporter>();

        (int Character, IReadOnlyList<int> Types)? raised = null;
        importer.ImplantsChanged += (c, t) => raised = (c, t);

        await importer.ImportAsync(77, TestContext.Current.CancellationToken);

        Assert.NotNull(raised); // without the event the badge only refreshes on the next list rebuild (re-auth/restart)
        Assert.Equal(77, raised!.Value.Character);
        Assert.Equal(new[] { 10, 20, 30 }, raised.Value.Types.OrderBy(id => id));
    }

    [Fact]
    public async Task ConcurrentImportsForTheSameCharacter_AreSerialised_NoUniqueRace()
    {
        var esi = new RoutingEsiClient();
        esi.Responses["/characters/77/implants/"] = new[] { 10, 20, 30 };
        var repository = new OverlapTrackingImplantRepository();
        var importer = new EsiImplantImporter(esi, repository);

        // The background refresh + on-demand import can hit the same character at once; without the per-character gate
        // the delete-then-insert overlaps and races into "UNIQUE constraint failed: CharacterImplant.*".
        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => importer.ImportAsync(77, TestContext.Current.CancellationToken)));

        Assert.Equal(1, repository.PeakOverlap); // serialised → at most one ReplaceForCharacterAsync runs at a time
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

    /// <summary>Records the peak number of <see cref="ReplaceForCharacterAsync"/> calls running at once, so the test
    /// can assert the per-character gate serialises them (peak 1) instead of letting the delete-then-insert race.</summary>
    private sealed class OverlapTrackingImplantRepository : ICharacterImplantRepository
    {
        private readonly object _lock = new();
        private int _current;
        public int PeakOverlap { get; private set; }

        public async Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<int> implantTypeIds, CancellationToken cancellationToken = default)
        {
            lock (_lock) { _current++; PeakOverlap = System.Math.Max(PeakOverlap, _current); }
            await Task.Delay(15, cancellationToken); // widen the window so an unserialised overlap would be observed
            lock (_lock) { _current--; }
        }

        public Task<IReadOnlyList<int>> GetTypeIdsAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<int>)System.Array.Empty<int>());

        public Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
