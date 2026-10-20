using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Events;

namespace EveUtils.Client.Runs;

/// <summary>
/// The pilot's own earnings figures of one activity, kept between the clicks that ask for them (ET-520). Reading them
/// walks every run of the activity and prices its loot, and the run drawer and the run details ask on each run opened,
/// yet they only change when a run does: a saved, edited, deleted or re-valued run drops everything held here.
/// </summary>
public sealed class OwnEarningsCache : ISingletonService
{
    // Loot is valued at today's price, which moves on its own: a figure is also dropped after the interval a server's are.
    private readonly ConcurrentDictionary<ActivityStatsKey, (ExpectedEarnings Figures, DateTime AtUtc)> _figures = [];
    private int _generation;

    public OwnEarningsCache(IEventBus eventBus) =>
        eventBus.Subscribe<RunsChangedEvent>(_ =>
        {
            Interlocked.Increment(ref _generation);
            _figures.Clear();
        });

    public async Task<ExpectedEarnings> GetAsync(ActivityStatsKey key, Func<Task<ExpectedEarnings>> load)
    {
        if (_figures.TryGetValue(key, out (ExpectedEarnings Figures, DateTime AtUtc) held)
            && DateTime.UtcNow - held.AtUtc < ActivityStatsService.CacheFor)
            return held.Figures;

        int generation = Volatile.Read(ref _generation);
        ExpectedEarnings read = await load();
        // A run changed while this was being read: shown, but not kept.
        if (generation == Volatile.Read(ref _generation))
            _figures[key] = (read, DateTime.UtcNow);
        return read;
    }
}
