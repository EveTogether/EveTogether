using System;
using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>The tier and weather each open run window holds for its run (ET-436). They reach the run only at SAVE
/// (ET-241), so while it is on the clock this is where the Local API's <c>runs/current</c> reads them from.</summary>
public sealed class RunningAbyssalPockets : ISingletonService
{
    private readonly ConcurrentDictionary<Guid, (int Tier, string Weather)> _byRun = new();

    /// <summary>Raised on the window's thread after a run's tier or weather changed.</summary>
    public event Action? Changed;

    public void Set(Guid runId, int? tier, string? weather)
    {
        if (tier is { } knownTier && weather is { Length: > 0 } knownWeather)
            _byRun[runId] = (knownTier, knownWeather);
        else
            _byRun.TryRemove(runId, out _);
        Changed?.Invoke();
    }

    public (int Tier, string Weather)? Of(Guid runId) =>
        _byRun.TryGetValue(runId, out (int Tier, string Weather) pocket) ? pocket : null;
}
