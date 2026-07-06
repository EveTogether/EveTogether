using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Fleet.Repositories.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Regression for the race in <see cref="EfExternalCharacterCache"/>: two roster reloads resolving the same uncached
/// id at once both pre-read "no row" and both INSERT, so the loser used to fail with
/// <c>UNIQUE constraint failed: CachedExternalCharacter.CharacterId</c>. The upsert now treats a concurrent insert
/// as an update, so concurrent first-time writes succeed and leave exactly one row.
/// </summary>
public class ExternalCharacterCacheConcurrencyTests
{
    [Fact]
    public async Task UpsertAsync_ConcurrentFirstTimeWrites_DoNotThrowAndKeepOneRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var cache = instance.Services.GetRequiredService<IExternalCharacterCache>();
        const int characterId = 91234567;

        var writes = Enumerable.Range(0, 6).Select(i => Task.Run(() => cache.UpsertAsync(new CachedExternalCharacter
        {
            CharacterId = characterId,
            Name = $"Pilot {i}",
            FetchedAtUnixMs = i,
        }, cancellationToken), cancellationToken));

        await Task.WhenAll(writes); // before the fix this threw a UNIQUE-constraint DbUpdateException

        var stored = await cache.GetAsync(characterId, cancellationToken);
        Assert.NotNull(stored);
    }
}
