using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="ISolarSystemNames"/> over the public <c>GET /universe/systems/{id}/</c> — no token and no scope, so
/// it answers for any id and works with nobody signed in.
///
/// Three things keep this thrifty, which matters because the caller (ET-63's location bootstrap) is driven by a
/// 6 s poll and would otherwise ask again every tick:
/// <list type="bullet">
/// <item>A resolved name is kept for the rest of the session. A solar system's name never changes, so the same id
/// is never asked twice however many characters stand in it.</item>
/// <item>Callers asking for the same unresolved id at once share one request. Every watch starts together at
/// start-up, so six characters in one system is one lookup, not six.</item>
/// <item>A failure is held for <see cref="RetryAfter"/>. Without that pause an ESI outage would turn one unfilled
/// gap into ten requests a minute for as long as it lasted; with it the gap still closes on its own once ESI
/// comes back, just not instantly.</item>
/// </list>
///
/// Underneath all three, the shared pivot's <see cref="EsiCacheHandler"/> keeps the answer on disk for the TTL ESI
/// gives it, so the first lookup of a session is often free too.
/// </summary>
public sealed class SolarSystemNames(IEsiClient esi, ILogger<SolarSystemNames> logger) : ISolarSystemNames, ISingletonService
{
    /// <summary>How long a failed lookup is left alone. Init-only so tests can drive the expiry.</summary>
    internal TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Init-only so a test can age a failure without waiting on the wall clock.</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    private readonly ConcurrentDictionary<int, string> _names = new();
    private readonly ConcurrentDictionary<int, DateTime> _failedAt = new();

    // Lazy, not the plain task: ConcurrentDictionary may run a GetOrAdd factory more than once under contention and
    // would then send the very requests this exists to collapse. Lazy's default mode runs its factory exactly once.
    private readonly ConcurrentDictionary<int, Lazy<Task<string?>>> _inFlight = new();

    public Task<string?> NameAsync(int solarSystemId)
    {
        if (solarSystemId <= 0)
            return Task.FromResult<string?>(null);

        if (_names.TryGetValue(solarSystemId, out var known))
            return Task.FromResult<string?>(known);

        if (_failedAt.TryGetValue(solarSystemId, out var failedAt) && Clock() - failedAt < RetryAfter)
            return Task.FromResult<string?>(null);

        return _inFlight.GetOrAdd(solarSystemId, id => new Lazy<Task<string?>>(() => ResolveAsync(id))).Value;
    }

    private async Task<string?> ResolveAsync(int solarSystemId)
    {
        try
        {
            var result = await esi.GetAsync<EsiSolarSystem>($"/universe/systems/{solarSystemId}/");

            if (result is { IsSuccess: true, Value.Name: { Length: > 0 } name })
            {
                _failedAt.TryRemove(solarSystemId, out _);
                return _names.GetOrAdd(solarSystemId, name);
            }

            _failedAt[solarSystemId] = Clock();
            logger.LogDebug("Could not resolve solar system {SolarSystemId}: {Error}.", solarSystemId, result.Error?.Kind);
            return null;
        }
        catch (Exception ex)
        {
            _failedAt[solarSystemId] = Clock();
            logger.LogDebug(ex, "Could not resolve solar system {SolarSystemId}.", solarSystemId);
            return null;
        }
        finally
        {
            // Only the in-flight slot goes: the outcome lives in _names or _failedAt, which is what the next
            // caller reads. Leaving the completed task here would pin the first answer for the whole session,
            // including a failure that RetryAfter is meant to let go of.
            _inFlight.TryRemove(solarSystemId, out _);
        }
    }
}
