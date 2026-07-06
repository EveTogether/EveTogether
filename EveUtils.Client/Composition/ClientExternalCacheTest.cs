using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless proof for the external-character 1-day SQLite cache, runnable via <c>--external-cache-test</c>.
/// Drives the real client DI's persistent <see cref="IExternalCharacterCache"/> (SQLite) but swaps a counting stub
/// for the public-ESI fetch and a movable clock, so it can prove: a first lookup hits ESI and writes the cache; a
/// second lookup within 24h is served from the cache with no further ESI call; after the clock advances past a day
/// the lookup re-fetches from ESI. Because it touches the client DB, run it on a throwaway instance:
/// <c>EVEUTILS_INSTANCE=tmp3a dotnet run --project EveUtils.Client -- --external-cache-test</c>. Exit 0 = pass, 1 = fail.
/// </summary>
public static class ClientExternalCacheTest
{
    private const int CharacterId = 91234567;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils external-character 1-day cache check ==");

        var cache = services.GetRequiredService<IExternalCharacterCache>(); // real SQLite-backed store
        var esi = new CountingEsiSource();
        var clock = new MovableClock(DateTimeOffset.UnixEpoch.AddYears(56)); // arbitrary fixed "now"
        var lookup = new ExternalCharacterLookup(esi, cache, clock);
        var ct = CancellationToken.None;

        var ok = true;

        // --- 1st lookup: cache cold → one ESI fetch + a cache write. ---
        var first = await lookup.LookupAsync(CharacterId, ct);
        ok &= Check("first lookup resolves the character", first is { Exists: true, Name: "Stub Pilot" });
        ok &= Check("first lookup hit ESI exactly once", esi.Calls == 1);
        ok &= Check("first lookup wrote the cache", await cache.GetAsync(CharacterId, ct) is not null);

        // --- 2nd lookup within 24h: served from cache, NO second ESI call. ---
        clock.Advance(TimeSpan.FromHours(23)); // still < 1 day
        var second = await lookup.LookupAsync(CharacterId, ct);
        ok &= Check("second lookup (within 24h) still resolves", second is { Exists: true, Name: "Stub Pilot" });
        ok &= Check("second lookup served from cache → no extra ESI call", esi.Calls == 1);

        // --- After 24h: the row is stale → a fresh ESI fetch. ---
        clock.Advance(TimeSpan.FromHours(2)); // now 25h since the write → stale
        var third = await lookup.LookupAsync(CharacterId, ct);
        ok &= Check("third lookup (after a day) re-fetches from ESI", esi.Calls == 2);
        ok &= Check("third lookup carries the refreshed info", third is { Exists: true });

        // --- A 4th lookup right after the re-fetch is fresh again → no further ESI call. ---
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await lookup.LookupAsync(CharacterId, ct);
        ok &= Check("the re-fetch reset the TTL (next lookup is cache-served)", esi.Calls == 2);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    /// <summary>A public-ESI stub that always resolves the same character and counts how often it is called.</summary>
    private sealed class CountingEsiSource : IExternalCharacterEsiSource
    {
        public int Calls { get; private set; }

        public Task<ExternalCharacterInfo> FetchAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ExternalCharacterInfo(characterId, "Stub Pilot", "Stub Corp", "Stub Alliance", Exists: true));
        }
    }

    /// <summary>A hand-cranked <see cref="TimeProvider"/> so the test can simulate the passage of a day.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
