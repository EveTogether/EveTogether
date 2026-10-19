using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Runs;

/// <summary>
/// Shares the outcome of a saved run with a server the pilot opted in to (ET-515, epic ET-512): the moment a run is
/// saved or corrected, in the background, with a retry that backs off. Apart from the EVE Workbench upload (ET-325) and
/// from the fleet run publishing: its own setting, its own payload (<see cref="IServerRunOutcomeClient"/>), its own queue.
///
/// <para><b>What goes, and where.</b> Nothing until the pilot chose to share with a server — the one question is asked
/// once per server, after the first run that could be shared. An activity goes to exactly one server: the one it
/// already went to, else its fleet's or the one a run was published to, else the server holding the most of this pilot's
/// outcomes. A fleet is one activity however many of its pilots share it; the first to share owns it.</para>
///
/// <para><b>The records are the queue.</b> What is to be sent is written down (<see cref="OutcomeShareRecordDto"/>)
/// before anything is sent, so an unreachable server only means it waits for the pilot's reconnect or the next retry.
/// A correction goes again under a higher revision, replacing the outcome rather than adding one; a run deleted or
/// discarded after it was shared sends a tombstone. Turning sharing off withdraws everything the pilot's characters
/// shared with that server.</para>
/// </summary>
public sealed class RunOutcomePublisher : ISingletonService, IDisposable
{
    /// <summary>How far back an opt-in offers to share earlier runs.</summary>
    public const int BackfillDays = 90;

    private static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    private static readonly TimeSpan SwitchedOffRecheck = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerRunOutcomeClient _client;
    private readonly IRemoteBusConnector _connector;
    private readonly RunOutcomeSharingChoices _choices;
    private readonly ILogger<RunOutcomePublisher> _logger;
    private readonly IDisposable _runsChanged;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _changedRefs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _switchedOff = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Server, string ActivityRef), DateTime> _nextAttempt = [];
    private Task _work = Task.CompletedTask;
    private CancellationTokenSource? _retryTimer;

    public RunOutcomePublisher(IEventBus eventBus, IServiceScopeFactory scopeFactory, IServerRunOutcomeClient client,
        IRemoteBusConnector connector, RunOutcomeSharingChoices choices, ILogger<RunOutcomePublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _connector = connector;
        _choices = choices;
        _logger = logger;
        _runsChanged = eventBus.Subscribe<RunsChangedEvent>(_OnRunsChangedAsync);
        _connector.CharacterStateChanged += _OnCharacterStateChanged;
    }

    /// <summary>The wait before each retry, by how many tries failed so far; the last step repeats. A test shortens it.</summary>
    internal IReadOnlyList<TimeSpan> Backoff { get; set; } = DefaultBackoff;

    /// <summary>Completes once nothing is left to do right now — for a test to wait on, since the work never holds up
    /// the save that caused it. A retry still waiting out its backoff is not "to do": it has its own timer.</summary>
    public async Task WhenIdleAsync()
    {
        Task current;
        do
        {
            lock (_gate)
                current = _work;
            await current;
        } while (!_IsLast(current));
    }

    public void Dispose()
    {
        _runsChanged.Dispose();
        _connector.CharacterStateChanged -= _OnCharacterStateChanged;
        lock (_gate)
            _retryTimer?.Cancel();
    }

    /// <summary>Where sharing stands with <paramref name="serverAddress"/>, for the settings.</summary>
    public async Task<OutcomeSharingStatus> StatusAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        OutcomeSharingChoice choice = (await _choices.LoadAsync(cancellationToken)).GetValueOrDefault(serverAddress);
        OutcomeShareRecordDto[] records = [.. (await dispatcher.Query(new GetOutcomeShareRecordsQuery(serverAddress), cancellationToken)).Value ?? []];
        OutcomeShareRecordDto[] shared = [.. records.Where(record => record.State is OutcomeShareState.Shared)];
        string? problem;
        lock (_gate)
            problem = _switchedOff.ContainsKey(serverAddress)
                ? "The server has switched earnings sharing off."
                : records.Where(record => record.LastError is not null).Select(record => record.LastError).FirstOrDefault();
        return new OutcomeSharingStatus(serverAddress, choice, shared.Length,
            shared.Select(record => record.SharedAtUtc).Max(),
            records.Count(record => record.State is OutcomeShareState.Pending or OutcomeShareState.RetractPending), problem);
    }

    /// <summary>How many earlier activities (within <see cref="BackfillDays"/>) opting in could still share with the server.</summary>
    public async Task<int> BackfillCountAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        return (await _BackfillRecordsAsync(scope.ServiceProvider, serverAddress, cancellationToken)).Count;
    }

    /// <summary>Records the pilot's answer for <paramref name="serverAddress"/>. Opting in starts sharing, with the
    /// earlier runs too when asked; opting out withdraws what was shared. Returns once the answer is recorded, with the
    /// sharing or the withdrawal following in the background.</summary>
    public async Task SetChoiceAsync(string serverAddress, OutcomeSharingChoice choice, bool backfill,
        CancellationToken cancellationToken = default)
    {
        OutcomeSharingChoice previous = (await _choices.LoadAsync(cancellationToken)).GetValueOrDefault(serverAddress);
        if (previous == choice)
            return;

        await _choices.SetAsync(serverAddress, choice, cancellationToken);
        lock (_gate)
            _switchedOff.Remove(serverAddress);
        if (choice is OutcomeSharingChoice.Shared)
            _ = _Enqueue(async token =>
            {
                if (backfill)
                    await _BackfillAsync(serverAddress, token);
                await _FlushAsync(token);
            });
        else if (previous is OutcomeSharingChoice.Shared)
            _ = _Enqueue(token => _WithdrawAsync(serverAddress, token));
    }

    private bool _IsLast(Task work)
    {
        lock (_gate)
            return ReferenceEquals(work, _work);
    }

    /// <summary>Noted and left: a SAVE must not wait on a network call, and a burst of changes folds into the one look
    /// that is still waiting its turn.</summary>
    private Task _OnRunsChangedAsync(RunsChangedEvent changed, CancellationToken cancellationToken)
    {
        string? activityRef = changed.Data.GroupCode ?? changed.Data.RunId?.ToString();
        if (activityRef is null)
            return Task.CompletedTask;

        bool firstOfItsTurn;
        lock (_gate)
        {
            firstOfItsTurn = _changedRefs.Count == 0;
            _changedRefs.Add(activityRef);
        }

        if (firstOfItsTurn)
            _ = _Enqueue(_ProcessChangedAsync);
        return Task.CompletedTask;
    }

    private void _OnCharacterStateChanged(string serverAddress, int characterId, ServerConnectionState state)
    {
        if (state != ServerConnectionState.Connected)
            return;

        lock (_gate)
            _switchedOff.Remove(serverAddress);
        _ = _Enqueue(_FlushAsync);
    }

    /// <summary>One piece of work at a time, in the order asked: two looks at the same activity would only race each other's records.</summary>
    private Task _Enqueue(Func<CancellationToken, Task> work)
    {
        lock (_gate)
            return _work = _work.ContinueWith(_ => _RunAsync(work), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task _RunAsync(Func<CancellationToken, Task> work)
    {
        try
        {
            await work(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Background work nobody awaits: logged, and the next piece still runs.
            _logger.LogError(exception, "Sharing run outcomes failed");
        }
    }

    private async Task _ProcessChangedAsync(CancellationToken cancellationToken)
    {
        string[] activityRefs;
        lock (_gate)
        {
            activityRefs = [.. _changedRefs];
            _changedRefs.Clear();
        }

        if (activityRefs.Length == 0)
            return;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        IDispatcher dispatcher = services.GetRequiredService<IDispatcher>();
        IReadOnlyList<ActivityOutcomeShareDto> activities = (await dispatcher.Query(
            new GetOutcomeSharesQuery(DateTime.UtcNow, activityRefs), cancellationToken)).Value ?? [];
        IReadOnlyList<OutcomeShareRecordDto> records = (await dispatcher.Query(new GetOutcomeShareRecordsQuery(), cancellationToken)).Value ?? [];
        IReadOnlyDictionary<string, OutcomeSharingChoice> choices = await _choices.LoadAsync(cancellationToken);
        IClientSessionStore sessions = services.GetRequiredService<IClientSessionStore>();

        List<OutcomeShareRecordDto> toSave = [];
        List<(string Server, string ActivityRef)> toForget = [];
        foreach (string activityRef in activityRefs)
        {
            OutcomeShareRecordDto[] held = [.. records.Where(record => record.ActivityRef == activityRef)];
            ActivityOutcomeShareDto? activity = activities.FirstOrDefault(candidate => candidate.ActivityRef == activityRef);
            if (activity?.Outcome is null)
            {
                _Retract(held, activity, toSave, toForget);
                continue;
            }

            if (activity.Outcome.CompletedOn < DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RunOutcomesModule.WidestWindowDays))
                continue;

            string[] coupled = [.. await sessions.ListServersForCharacterAsync(_SessionCharacter(activity.CharacterId), cancellationToken)];
            string? server = _PickServer(activity, held, records, coupled, choices);
            if (server is null)
            {
                await _OfferAsync(activity, coupled, choices, services, cancellationToken);
                continue;
            }

            OutcomeShareRecordDto? current = held.FirstOrDefault(record => record.ServerAddress == server);
            if (current is not null && current.SourceRevision == activity.Revision
                                    && current.State is not OutcomeShareState.RetractPending)
                continue;

            toSave.Add(new OutcomeShareRecordDto
            {
                ServerAddress = server,
                ActivityRef = activityRef,
                CharacterId = activity.CharacterId,
                State = OutcomeShareState.Pending,
                SourceRevision = activity.Revision,
                SentRevision = Math.Max(activity.Revision, (current?.SentRevision ?? 0) + 1),
                SharedAtUtc = current?.SharedAtUtc
            });
        }

        await _PersistAsync(dispatcher, toSave, toForget, cancellationToken);
        await _FlushAsync(cancellationToken);
    }

    /// <summary>The activity can no longer be shared: what was sent is taken back, what never was is forgotten.</summary>
    private static void _Retract(OutcomeShareRecordDto[] held, ActivityOutcomeShareDto? activity,
        List<OutcomeShareRecordDto> toSave, List<(string Server, string ActivityRef)> toForget)
    {
        foreach (OutcomeShareRecordDto record in held)
            if (record.SharedAtUtc is not null || record.State is OutcomeShareState.RetractPending)
                toSave.Add(new OutcomeShareRecordDto
                {
                    ServerAddress = record.ServerAddress,
                    ActivityRef = record.ActivityRef,
                    CharacterId = record.CharacterId,
                    State = OutcomeShareState.RetractPending,
                    SourceRevision = activity?.Revision ?? record.SourceRevision,
                    SentRevision = Math.Max(activity?.Revision ?? 0, record.SentRevision),
                    SharedAtUtc = record.SharedAtUtc
                });
            else
                toForget.Add((record.ServerAddress, record.ActivityRef));
    }

    /// <summary>
    /// The one server an activity goes to, among those the pilot opted in to and this pilot is coupled to: where it
    /// already went; else its fleet's, or where a run was published; else the one holding the most of this pilot's
    /// outcomes, the nearest to "the server with the most data" the advice asks for (ET-512, decision 10).
    /// </summary>
    private static string? _PickServer(ActivityOutcomeShareDto activity, OutcomeShareRecordDto[] held,
        IReadOnlyList<OutcomeShareRecordDto> allRecords, string[] coupled, IReadOnlyDictionary<string, OutcomeSharingChoice> choices)
    {
        string[] candidates = [.. coupled.Where(server => choices.GetValueOrDefault(server) is OutcomeSharingChoice.Shared)];
        if (held.FirstOrDefault(record => candidates.Contains(record.ServerAddress)) is { } already)
            return already.ServerAddress;
        if (activity.PreferredServer is { } preferred && candidates.Contains(preferred))
            return preferred;

        return candidates
            .OrderByDescending(server => allRecords.Count(record => record.ServerAddress == server && record.State is OutcomeShareState.Shared))
            .ThenBy(server => server, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The one question per server, asked after the first run that could be shared with it (Jithran 18-10).
    /// Asked again only after a restart if the pilot answered "not now".</summary>
    private async Task _OfferAsync(ActivityOutcomeShareDto activity, string[] coupled,
        IReadOnlyDictionary<string, OutcomeSharingChoice> choices, IServiceProvider services, CancellationToken cancellationToken)
    {
        string? server;
        lock (_gate)
        {
            server = coupled
                .Where(candidate => choices.GetValueOrDefault(candidate) is OutcomeSharingChoice.Undecided && !_asked.Contains(candidate))
                .OrderBy(candidate => candidate == activity.PreferredServer ? 0 : 1)
                .ThenBy(candidate => candidate, StringComparer.Ordinal)
                .FirstOrDefault();
            if (server is not null)
                _asked.Add(server);
        }

        if (server is null || services.GetService<IToastService>() is not { } toasts)
            return;

        string name = await services.GetRequiredService<IServerRegistry>().DisplayNameAsync(server, cancellationToken);
        toasts.Show($"Help {name} estimate site earnings?",
            "Shares only the outcome of your runs — loot, bounty and duration. No location, no names. You can change this in Settings › Privacy & Sharing.",
            ToastKind.Information,
            [
                new ToastAction("SHARE", () => _ = _AcceptOfferAsync(server, name), ToastActionStyle.Affirmative),
                new ToastAction("NOT NOW", () => { }),
                new ToastAction("NEVER", () => _ = _choices.SetAsync(server, OutcomeSharingChoice.Never), ToastActionStyle.Destructive)
            ]);
    }

    private async Task _AcceptOfferAsync(string server, string name)
    {
        int earlier = await BackfillCountAsync(server);
        await SetChoiceAsync(server, OutcomeSharingChoice.Shared, backfill: true);
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetService<IToastService>()?.Show($"Sharing with {name}.",
            earlier > 0 ? $"{earlier} earlier {(earlier == 1 ? "run" : "runs")} added." : null);
    }

    private async Task _BackfillAsync(string serverAddress, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IReadOnlyList<OutcomeShareRecordDto> records = await _BackfillRecordsAsync(scope.ServiceProvider, serverAddress, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SaveOutcomeShareRecordsCommand(records), cancellationToken);
    }

    /// <summary>The activities of the last <see cref="BackfillDays"/> days this server could still be given: not yet
    /// held by any server, and by a pilot coupled to it.</summary>
    private async Task<IReadOnlyList<OutcomeShareRecordDto>> _BackfillRecordsAsync(IServiceProvider services, string serverAddress,
        CancellationToken cancellationToken)
    {
        IDispatcher dispatcher = services.GetRequiredService<IDispatcher>();
        IClientSessionStore sessions = services.GetRequiredService<IClientSessionStore>();
        HashSet<string> held = [.. ((await dispatcher.Query(new GetOutcomeShareRecordsQuery(), cancellationToken)).Value ?? [])
            .Select(record => record.ActivityRef)];
        HashSet<long> pilots = [.. (await sessions.LoadAllAsync(serverAddress, cancellationToken)).Select(session => (long)session.CharacterId)];
        DateOnly oldest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RunOutcomesModule.WidestWindowDays);

        return
        [
            .. ((await dispatcher.Query(new GetOutcomeSharesQuery(DateTime.UtcNow.AddDays(-BackfillDays)), cancellationToken)).Value ?? [])
            .Where(activity => activity.Outcome is { } outcome && outcome.CompletedOn >= oldest
                               && !held.Contains(activity.ActivityRef) && pilots.Contains(activity.CharacterId))
            .Select(activity => new OutcomeShareRecordDto
            {
                ServerAddress = serverAddress,
                ActivityRef = activity.ActivityRef,
                CharacterId = activity.CharacterId,
                State = OutcomeShareState.Pending,
                SourceRevision = activity.Revision,
                SentRevision = activity.Revision
            })
        ];
    }

    /// <summary>Sends whatever is waiting: the new and corrected outcomes, and the tombstones. A server that cannot be
    /// reached, or a pilot not connected, only leaves the record waiting.</summary>
    private async Task _FlushAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        OutcomeShareRecordDto[] waiting = [.. ((await dispatcher.Query(new GetOutcomeShareRecordsQuery(), cancellationToken)).Value ?? [])
            .Where(record => record.State is OutcomeShareState.Pending or OutcomeShareState.RetractPending)];
        if (waiting.Length == 0)
            return;

        IReadOnlyDictionary<string, OutcomeSharingChoice> choices = await _choices.LoadAsync(cancellationToken);
        string[] pendingRefs = [.. waiting.Where(record => record.State is OutcomeShareState.Pending).Select(record => record.ActivityRef).Distinct()];
        IReadOnlyList<ActivityOutcomeShareDto> activities = pendingRefs.Length == 0
            ? []
            : (await dispatcher.Query(new GetOutcomeSharesQuery(DateTime.UtcNow, pendingRefs), cancellationToken)).Value ?? [];

        List<OutcomeShareRecordDto> toSave = [];
        List<(string Server, string ActivityRef)> toForget = [];
        TimeSpan? retryIn = null;
        DateTime now = DateTime.UtcNow;
        foreach (OutcomeShareRecordDto record in waiting)
        {
            (string, string) key = (record.ServerAddress, record.ActivityRef);
            if (_IsHeldBack(key, record.ServerAddress, now) || !_IsConnected(record))
                continue;

            if (record.State is OutcomeShareState.Pending
                && choices.GetValueOrDefault(record.ServerAddress) is not OutcomeSharingChoice.Shared)
            {
                toForget.Add(key);
                continue;
            }

            RunOutcomeShareDto? outcome = null;
            if (record.State is OutcomeShareState.Pending)
            {
                outcome = activities.FirstOrDefault(activity => activity.ActivityRef == record.ActivityRef)?.Outcome;
                if (outcome is null)
                {
                    // Changed under us since it was queued: gone if it never went out, a tombstone if it did.
                    if (record.SharedAtUtc is null)
                        toForget.Add(key);
                    else
                        toSave.Add(_With(record, OutcomeShareState.RetractPending));
                    continue;
                }
            }

            OutcomeCallResult result = await _client.ShareAsync(record.ServerAddress, record.CharacterId, record.ActivityRef,
                record.SentRevision, outcome, cancellationToken);
            if (result.Accepted)
                _Accepted(record, result, now, toSave, toForget);
            else if (result.Code == MessageCodes.PermissionDenied)
                lock (_gate)
                    _switchedOff[record.ServerAddress] = now + SwitchedOffRecheck;
            else if (result.IsTransient)
            {
                int attempts = record.Attempts + 1;
                TimeSpan wait = Backoff[Math.Min(attempts - 1, Backoff.Count - 1)];
                lock (_gate)
                    _nextAttempt[key] = now + wait;
                retryIn = retryIn is { } soonest && soonest <= wait ? soonest : wait;
                toSave.Add(_With(record, record.State, attempts, result.Message));
            }
            else if (record.State is OutcomeShareState.Pending)
                toSave.Add(_With(record, OutcomeShareState.Rejected, record.Attempts, result.Message));
            else
                toForget.Add(key);
        }

        await _PersistAsync(dispatcher, toSave, toForget, cancellationToken);
        if (retryIn is { } delay)
            _ScheduleRetry(delay);
    }

    private bool _IsHeldBack((string Server, string ActivityRef) key, string serverAddress, DateTime now)
    {
        lock (_gate)
        {
            if (_switchedOff.TryGetValue(serverAddress, out DateTime until) && until > now)
                return true;
            return _nextAttempt.TryGetValue(key, out DateTime due) && due > now;
        }
    }

    private bool _IsConnected(OutcomeShareRecordDto record) =>
        record.CharacterId is > 0 and <= int.MaxValue
        && _connector.StateFor(record.ServerAddress, (int)record.CharacterId) == ServerConnectionState.Connected;

    private void _Accepted(OutcomeShareRecordDto record, OutcomeCallResult result, DateTime now,
        List<OutcomeShareRecordDto> toSave, List<(string Server, string ActivityRef)> toForget)
    {
        lock (_gate)
            _nextAttempt.Remove((record.ServerAddress, record.ActivityRef));
        if (record.State is OutcomeShareState.RetractPending)
        {
            toForget.Add((record.ServerAddress, record.ActivityRef));
            return;
        }

        // Stale: the server already holds this revision or a newer one of ours; nothing further is owed.
        OutcomeShareState state = result.Status is RunOutcomeShareStatus.SharedByAnother
            ? OutcomeShareState.Covered
            : OutcomeShareState.Shared;
        toSave.Add(new OutcomeShareRecordDto
        {
            ServerAddress = record.ServerAddress,
            ActivityRef = record.ActivityRef,
            CharacterId = record.CharacterId,
            State = state,
            SourceRevision = record.SourceRevision,
            SentRevision = record.SentRevision,
            SharedAtUtc = state is OutcomeShareState.Shared ? now : null
        });
    }

    private static OutcomeShareRecordDto _With(OutcomeShareRecordDto record, OutcomeShareState state, int? attempts = null,
        string? lastError = null) => new()
    {
        ServerAddress = record.ServerAddress,
        ActivityRef = record.ActivityRef,
        CharacterId = record.CharacterId,
        State = state,
        SourceRevision = record.SourceRevision,
        SentRevision = record.SentRevision,
        SharedAtUtc = record.SharedAtUtc,
        Attempts = attempts ?? record.Attempts,
        LastError = lastError
    };

    private static async Task _PersistAsync(IDispatcher dispatcher, List<OutcomeShareRecordDto> toSave,
        List<(string Server, string ActivityRef)> toForget, CancellationToken cancellationToken)
    {
        await dispatcher.Send(new SaveOutcomeShareRecordsCommand(toSave), cancellationToken);
        foreach (IGrouping<string, string> server in toForget.GroupBy(entry => entry.Server, entry => entry.ActivityRef))
            await dispatcher.Send(new DeleteOutcomeShareRecordsCommand(server.Key, [.. server]), cancellationToken);
    }

    private void _ScheduleRetry(TimeSpan delay)
    {
        lock (_gate)
        {
            _retryTimer?.Cancel();
            _retryTimer = new CancellationTokenSource();
            CancellationToken token = _retryTimer.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _Enqueue(_FlushAsync);
            }, CancellationToken.None);
        }
    }

    /// <summary>The opt-out: every pilot who shared with the server withdraws their outcomes. A pilot who is not
    /// connected right now keeps theirs queued as tombstones, sent at the next connection.</summary>
    private async Task _WithdrawAsync(string serverAddress, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        IDispatcher dispatcher = services.GetRequiredService<IDispatcher>();
        OutcomeShareRecordDto[] records = [.. (await dispatcher.Query(new GetOutcomeShareRecordsQuery(serverAddress), cancellationToken)).Value ?? []];

        int removed = 0;
        List<OutcomeShareRecordDto> stillOwed = [];
        foreach (IGrouping<long, OutcomeShareRecordDto> pilot in records
                     .Where(record => record.SharedAtUtc is not null)
                     .GroupBy(record => record.CharacterId))
        {
            OutcomeCallResult result = _IsConnected(pilot.First())
                ? await _client.WithdrawAsync(serverAddress, pilot.Key, cancellationToken)
                : OutcomeCallResult.Transient("Not connected.");
            if (result.Accepted)
                removed += result.Count;
            else
                stillOwed.AddRange(pilot.Select(record => _With(record, OutcomeShareState.RetractPending)));
        }

        await dispatcher.Send(new DeleteOutcomeShareRecordsCommand(serverAddress), cancellationToken);
        await dispatcher.Send(new SaveOutcomeShareRecordsCommand(stillOwed), cancellationToken);
        if (stillOwed.Count > 0)
            _ScheduleRetry(Backoff[0]);

        if (removed == 0 && stillOwed.Count == 0 || services.GetService<IToastService>() is not { } toasts)
            return;

        string name = await services.GetRequiredService<IServerRegistry>().DisplayNameAsync(serverAddress, cancellationToken);
        if (stillOwed.Count == 0)
            toasts.Show($"Your {removed} shared {(removed == 1 ? "outcome was" : "outcomes were")} removed from {name}.");
        else
            toasts.Show($"Not everything could be removed from {name} yet.",
                "It is removed as soon as the pilot who shared it is connected to the server again.", ToastKind.Warning);
    }

    private static int _SessionCharacter(long characterId) =>
        characterId is > 0 and <= int.MaxValue ? (int)characterId : 0;
}

/// <param name="Problem">Why sharing is not going through right now, or null while it is.</param>
public sealed record OutcomeSharingStatus(string ServerAddress, OutcomeSharingChoice Choice, int SharedCount,
    DateTime? LastSharedAtUtc, int PendingCount, string? Problem);
