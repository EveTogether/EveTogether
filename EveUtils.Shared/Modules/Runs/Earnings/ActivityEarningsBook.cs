using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// The earnings samples of the own saved activities, kept between the reads that ask for them (ET-521). Reading them
/// walks every run of the last year with its loot, bounty and mining, and the runs overview asked for all of it again on
/// every batch of changes. A change to a run names the run, and its group where it has one, so only the activities
/// those belong to are read again; every other sample stays as it was.
///
/// <para>The subscriber only notes what changed and never reads: <c>InProcessEventBus</c> awaits it inside the command.
/// The next read takes the notes and applies them. A change that names no run (a rebuild of every summary) and the
/// age of the book (<see cref="MaxAge"/>, the interval the client's other earnings caches use) drop everything.</para>
/// </summary>
[ClientOnly]
internal sealed class ActivityEarningsBook : ISingletonService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly Lock _noted = new();
    private readonly HashSet<string> _changedRefs = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _changedRuns = [];
    private readonly Dictionary<string, ActivityEarningsSample> _samples = new(StringComparer.Ordinal);
    private bool _isEverythingChanged = true;
    private DateTime _coversFromUtc = DateTime.MaxValue;
    private DateTime _builtAtUtc;

    public ActivityEarningsBook(IEventBus eventBus) =>
        eventBus.Subscribe<RunsChangedEvent>(change => _Note(change.Data));

    /// <summary>How many activities the last read took from the database, for a test to tell a recompute from a reuse.</summary>
    public int ActivitiesRead { get; private set; }

    /// <summary>The samples of the activities that completed since <paramref name="sinceUtc"/>.</summary>
    public async Task<IReadOnlyList<ActivityEarningsSample>> ReadAsync(ClientDbContext db, ISdeAccessor sde, DateTime sinceUtc,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await _reading.WaitAsync(cancellationToken);
        try
        {
            await _BringUpToDateAsync(db, sde, sinceUtc, nowUtc, cancellationToken);
            return [.. _samples.Values.Where(sample => sample.Sample.CompletedAtUtc >= sinceUtc)];
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task _BringUpToDateAsync(ClientDbContext db, ISdeAccessor sde, DateTime sinceUtc, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        (bool isEverythingChanged, HashSet<string> changedRefs, HashSet<Guid> changedRuns) = _TakeNotes();
        try
        {
            if (isEverythingChanged || sinceUtc < _coversFromUtc || nowUtc - _builtAtUtc > MaxAge)
            {
                DateTime coversFromUtc = sinceUtc < _coversFromUtc ? sinceUtc : _coversFromUtc;
                IReadOnlyList<ActivityEarningsSample> all = await LocalEarningsSamples.ReadAsync(db, sde, coversFromUtc, nowUtc, cancellationToken);
                _samples.Clear();
                foreach (ActivityEarningsSample sample in all)
                    _samples[sample.ActivityRef] = sample;
                _coversFromUtc = coversFromUtc;
                _builtAtUtc = nowUtc;
                ActivitiesRead = all.Count;
                return;
            }

            ActivitiesRead = 0;
            if (changedRefs.Count == 0 && changedRuns.Count == 0)
                return;

            // An activity goes by its group code or, without one, its run's id, and every write names one or both.
            string[] refs = [.. changedRefs.Concat(changedRuns.Select(runId => runId.ToString())).Distinct(StringComparer.Ordinal)];
            IReadOnlyList<ActivityEarningsSample> read = await LocalEarningsSamples.ReadAsync(db, sde, DateTime.MinValue, nowUtc,
                cancellationToken, refs);
            foreach (string reference in refs)
                _samples.Remove(reference);
            foreach (ActivityEarningsSample sample in read)
                _samples[sample.ActivityRef] = sample;
            ActivitiesRead = read.Count;
        }
        catch
        {
            _Renote(isEverythingChanged, changedRefs, changedRuns);
            throw;
        }
    }

    private void _Note(RunsChangedEventData change)
    {
        lock (_noted)
        {
            if (change is { RunId: null, GroupCode: null })
                _isEverythingChanged = true;
            if (change.RunId is { } runId)
                _changedRuns.Add(runId);
            if (change.GroupCode is { } groupCode)
                _changedRefs.Add(groupCode);
        }
    }

    private (bool IsEverythingChanged, HashSet<string> Refs, HashSet<Guid> Runs) _TakeNotes()
    {
        lock (_noted)
        {
            var taken = (_isEverythingChanged, new HashSet<string>(_changedRefs, StringComparer.Ordinal), new HashSet<Guid>(_changedRuns));
            _isEverythingChanged = false;
            _changedRefs.Clear();
            _changedRuns.Clear();
            return taken;
        }
    }

    private void _Renote(bool isEverythingChanged, HashSet<string> refs, HashSet<Guid> runs)
    {
        lock (_noted)
        {
            _isEverythingChanged |= isEverythingChanged;
            _changedRefs.UnionWith(refs);
            _changedRuns.UnionWith(runs);
        }
    }
}
