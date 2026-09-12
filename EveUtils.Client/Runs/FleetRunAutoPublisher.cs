using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Runs;

/// <summary>
/// Publishes a fleet run to its own fleet's server the moment it is saved or corrected, and pulls a group mate's run
/// the moment the server says one arrived (ET-245). Until this existed the PUBLISH button was the only way either
/// happened, so whoever published first never saw the other's run without pressing it again.
///
/// <para><b>What goes by itself.</b> Only a run of this machine's own pilot, in a group whose fleet's server is
/// recorded (<see cref="RunGroupOrigin.ServerAddress"/>), with that pilot coupled to that server. A solo run, a run in a
/// client-only fleet, or one whose fleet's server could not be told for certain stays on the PUBLISH button — as does
/// every run once <see cref="EnabledSettingKey"/> is off. A run the pilot already published to another server is left
/// with that server.</para>
///
/// <para><b>The queue is the store.</b> A run to publish is marked queued (<see cref="RunSyncState.Pending"/>) before
/// anything is sent, so an unreachable server only means it waits: every reconnect of that pilot pushes what is still
/// queued and pulls what the fleet's other pilots published meanwhile. The server keys a run by id, so sending it again
/// never adds a second copy.</para>
///
/// <para><b>Why a notice and not a timer.</b> The server tells every character holding a run in the group, right after
/// it accepted a push — exactly the characters its pull answers, and still reachable after the fleet itself was
/// concluded, which is when most runs are saved. A timer would ask every few seconds whether anything changed while
/// nothing did, and would still be late. The one gap a notice leaves, a client that was offline, is what the pull on
/// reconnect closes.</para>
/// </summary>
public sealed class FleetRunAutoPublisher : ISingletonService, IDisposable
{
    /// <summary>"false" keeps every run on the PUBLISH button. Anything else, including no row, publishes.</summary>
    public const string EnabledSettingKey = "runs.auto-publish";

    /// <summary>How far back a reconnect looks for groups to catch up on: a group mate's run can be committed by the app
    /// itself a day after it stopped (ET-179), and a pilot can be away for a weekend in between.</summary>
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly IFleetParticipation _participation;
    private readonly IRemoteBusConnector _connector;
    private readonly RunChangeFeed _feed;
    private readonly ILogger<FleetRunAutoPublisher> _logger;
    private readonly IDisposable _runsChanged;
    private readonly IDisposable _groupUpdated;
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _changedRunIds = [];
    private readonly Dictionary<string, RunPublishProgress> _progress = new(StringComparer.Ordinal);
    private Task _work = Task.CompletedTask;

    public FleetRunAutoPublisher(IEventBus eventBus, IServiceScopeFactory scopeFactory,
        IDbContextFactory<ClientDbContext> contextFactory, IFleetParticipation participation,
        IRemoteBusConnector connector, RunChangeFeed feed, ILogger<FleetRunAutoPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _contextFactory = contextFactory;
        _participation = participation;
        _connector = connector;
        _feed = feed;
        _logger = logger;
        _runsChanged = eventBus.Subscribe<RunsChangedEvent>(_OnRunsChangedAsync);
        _groupUpdated = eventBus.Subscribe<RunGroupUpdatedEvent>(_OnGroupUpdatedAsync);
        _connector.CharacterStateChanged += _OnCharacterStateChanged;
    }

    /// <summary>The publish of <paramref name="groupCode"/> in flight or last failed, null while it is simply queued,
    /// done, or not this publisher's to do.</summary>
    public RunPublishProgress? ProgressFor(string? groupCode)
    {
        if (groupCode is null)
            return null;

        lock (_gate)
            return _progress.GetValueOrDefault(groupCode);
    }

    /// <summary>RETRY on a row whose publish failed: the group's own runs are looked at again exactly as if they had
    /// just changed, so a retry follows every rule a save does.</summary>
    public Task RetryAsync(string groupCode) => _Enqueue(async cancellationToken =>
    {
        List<Guid> runIds;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
            runIds = await db.Set<Run>().AsNoTracking()
                .Where(run => run.GroupCode == groupCode)
                .Select(run => run.Id)
                .ToListAsync(cancellationToken);
        lock (_gate)
            _changedRunIds.UnionWith(runIds);
        await _PublishChangedRunsAsync(cancellationToken);
    });

    /// <summary>Completes once nothing is left to do, including what the work itself set off (a push's own change
    /// signal) — for a test to wait on, since the work never holds up the command that caused it.</summary>
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

    private bool _IsLast(Task work)
    {
        lock (_gate)
            return ReferenceEquals(work, _work);
    }

    public void Dispose()
    {
        _runsChanged.Dispose();
        _groupUpdated.Dispose();
        _connector.CharacterStateChanged -= _OnCharacterStateChanged;
    }

    /// <summary>Noted and left: a SAVE must not wait on a network call, and a burst of changes (a pocket's payouts, a
    /// group SAVE of several own pilots) folds into the one look that is still waiting its turn.</summary>
    private Task _OnRunsChangedAsync(RunsChangedEvent changed, CancellationToken cancellationToken)
    {
        if (changed.Data.RunId is not { } runId)
            return Task.CompletedTask;

        bool firstOfItsTurn;
        lock (_gate)
        {
            firstOfItsTurn = _changedRunIds.Count == 0;
            _changedRunIds.Add(runId);
        }

        if (firstOfItsTurn)
            _ = _Enqueue(_PublishChangedRunsAsync);
        return Task.CompletedTask;
    }

    private Task _OnGroupUpdatedAsync(RunGroupUpdatedEvent updated, CancellationToken cancellationToken)
    {
        if (updated.SourceServerAddress is { } serverAddress)
            _ = _Enqueue(token => _PullGroupAsync(serverAddress, updated.Data.GroupCode, token));
        return Task.CompletedTask;
    }

    private void _OnCharacterStateChanged(string serverAddress, int characterId, ServerConnectionState state)
    {
        if (state == ServerConnectionState.Connected)
            _ = _Enqueue(token => _CatchUpAsync(serverAddress, characterId, token));
    }

    /// <summary>One piece of work at a time, in the order asked: two exchanges with one server for the same group would
    /// only race each other's pull.</summary>
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
            _logger.LogError(exception, "Automatic run publishing failed");
        }
    }

    private async Task _PublishChangedRunsAsync(CancellationToken cancellationToken)
    {
        Guid[] runIds;
        lock (_gate)
        {
            runIds = [.. _changedRunIds];
            _changedRunIds.Clear();
        }

        if (runIds.Length == 0)
            return;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        List<Run> runs;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
            runs = await db.Set<Run>().AsNoTracking()
                .Where(run => runIds.Contains(run.Id) && run.GroupCode != null)
                .ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return;

        // Before the setting is asked: which server a fleet lives on is a fact worth keeping either way, and only a
        // run still being flown is sure to find its fleet in participation.
        Dictionary<string, string?> serverByGroup = await _FleetServersAsync(scope.ServiceProvider,
            [.. runs.Select(run => run.GroupCode).OfType<string>().Distinct()], cancellationToken);
        if (!await _IsEnabledAsync(scope.ServiceProvider, cancellationToken))
            return;

        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        IClientSessionStore sessions = scope.ServiceProvider.GetRequiredService<IClientSessionStore>();
        HashSet<(string Server, long CharacterId, string GroupCode)> exchanges = [];
        foreach (Run run in runs)
        {
            if (run.GroupCode is not { } groupCode || serverByGroup.GetValueOrDefault(groupCode) is not { } serverAddress)
                continue;
            if (!_IsToPublish(run, serverAddress) || run.CharacterId is <= 0 or > int.MaxValue
                || await sessions.LoadForCharacterAsync(serverAddress, (int)run.CharacterId, cancellationToken) is null)
                continue;

            if (run.SyncState is RunSyncState.Local or RunSyncState.Outdated)
            {
                Result queued = await dispatcher.Send(new QueueRunForServerSyncCommand(run.Id, serverAddress), cancellationToken);
                if (!queued.IsSuccess)
                    continue;
            }
            exchanges.Add((serverAddress, run.CharacterId, groupCode));
        }

        foreach (IGrouping<(string Server, long CharacterId), string> exchange in exchanges
                     .GroupBy(entry => (entry.Server, entry.CharacterId), entry => entry.GroupCode))
            await _ExchangeAsync(scope.ServiceProvider, exchange.Key.Server, exchange.Key.CharacterId, [.. exchange],
                pushPending: true, cancellationToken);
    }

    /// <summary>Saved and not yet on the server as it stands, or deleted after it went there (the server has to hear
    /// that too). Never a running run, never one only ever kept here and then thrown away, and never one the pilot
    /// already sent to another server.</summary>
    private static bool _IsToPublish(Run run, string serverAddress)
    {
        if (run.SyncServerAddress is { } publishedTo && publishedTo != serverAddress)
            return false;

        return run.DeletedAtUtc is not null
            ? run.SyncState is RunSyncState.Pending
            : run.State is RunState.Saved && run.SyncState is not RunSyncState.Synced;
    }

    /// <summary>A server's notice that a run in <paramref name="groupCode"/> arrived: pulled with one of this machine's
    /// own pilots holding a run there, since the server only answers those.</summary>
    private async Task _PullGroupAsync(string serverAddress, string groupCode, CancellationToken cancellationToken)
    {
        List<long> holders;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
            holders = await db.Set<Run>().AsNoTracking()
                .Where(run => run.GroupCode == groupCode && run.SyncServerAddress == serverAddress && run.DeletedAtUtc == null)
                .Select(run => run.CharacterId)
                .Distinct()
                .ToListAsync(cancellationToken);
        long puller = holders.FirstOrDefault(characterId => _IsConnected(serverAddress, characterId));
        if (puller == 0)
            return;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        await _ExchangeAsync(scope.ServiceProvider, serverAddress, puller, [groupCode], pushPending: false, cancellationToken);
    }

    /// <summary>A pilot's connection to a server came (back) up. Whatever of theirs is still queued for that server in
    /// a group of a fleet living there goes, however old — the app's own save of a run left a day (ET-179) is often
    /// made before any connection exists. And the groups they published there lately are pulled for what the others
    /// added meanwhile: the notices sent while this client was away are gone for good.</summary>
    private async Task _CatchUpAsync(string serverAddress, int characterId, CancellationToken cancellationToken)
    {
        DateTime sinceUtc = DateTime.UtcNow - CatchUpWindow;
        List<string?> queuedGroups;
        List<string?> recentGroups;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            IQueryable<Run> heldThere = db.Set<Run>().AsNoTracking().Where(run =>
                run.CharacterId == characterId && run.GroupCode != null && run.SyncServerAddress == serverAddress);
            queuedGroups = await heldThere.Where(run => run.SyncState == RunSyncState.Pending)
                .Select(run => run.GroupCode).Distinct().ToListAsync(cancellationToken);
            recentGroups = await heldThere.Where(run => run.StartedAtUtc >= sinceUtc)
                .Select(run => run.GroupCode).Distinct().ToListAsync(cancellationToken);
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        List<string> pushGroups = [];
        if (queuedGroups.Count > 0 && await _IsEnabledAsync(scope.ServiceProvider, cancellationToken))
        {
            Dictionary<string, string?> serverByGroup = await _FleetServersAsync(scope.ServiceProvider,
                [.. queuedGroups.OfType<string>()], cancellationToken);
            pushGroups = [.. serverByGroup.Where(entry => entry.Value == serverAddress).Select(entry => entry.Key)];
        }

        List<string> pullOnlyGroups = [.. recentGroups.OfType<string>().Except(pushGroups)];
        if (pushGroups.Count > 0)
            await _ExchangeAsync(scope.ServiceProvider, serverAddress, characterId, pushGroups, pushPending: true,
                cancellationToken);
        if (pullOnlyGroups.Count > 0)
            await _ExchangeAsync(scope.ServiceProvider, serverAddress, characterId, pullOnlyGroups, pushPending: false,
                cancellationToken);
    }

    /// <summary>One push-and-pull with one server as one pilot. A pilot not connected to it right now is not an error:
    /// the runs stay queued, and that pilot's reconnect brings them along.</summary>
    private async Task _ExchangeAsync(IServiceProvider services, string serverAddress, long characterId,
        IReadOnlyCollection<string> groupCodes, bool pushPending, CancellationToken cancellationToken)
    {
        if (!_IsConnected(serverAddress, characterId))
            return;

        if (pushPending)
            _Report(groupCodes, new RunPublishProgress(serverAddress, RunPublishPhase.Publishing));
        (bool accepted, string message) outcome;
        try
        {
            outcome = await services.GetRequiredService<RunSynchronizationService>()
                .SynchronizeGroupsAsync(serverAddress, characterId, groupCodes, pushPending, cancellationToken);
        }
        catch (Exception exception)
        {
            // Said on the row rather than left as "publishing…" for good; the log keeps the detail.
            _logger.LogError(exception, "Publishing to {Server} failed", serverAddress);
            outcome = (false, exception.Message);
        }

        if (!outcome.accepted)
            _logger.LogWarning("Automatic run sync with {Server} was refused: {Message}", serverAddress, outcome.message);
        if (pushPending)
            _Report(groupCodes, outcome.accepted
                ? null
                : new RunPublishProgress(serverAddress, RunPublishPhase.Failed, outcome.message));
    }

    private void _Report(IReadOnlyCollection<string> groupCodes, RunPublishProgress? progress)
    {
        lock (_gate)
            foreach (string groupCode in groupCodes)
                if (progress is null)
                    _progress.Remove(groupCode);
                else
                    _progress[groupCode] = progress;

        foreach (string groupCode in groupCodes)
            _feed.Announce(groupCode);
    }

    private bool _IsConnected(string serverAddress, long characterId) =>
        characterId is > 0 and <= int.MaxValue
        && _connector.StateFor(serverAddress, (int)characterId) == ServerConnectionState.Connected;

    /// <summary>
    /// The server each group's fleet lives on, recorded the first time this client can tell it for certain and read
    /// back from then on — so a run saved a day later, long after its fleet was concluded and dropped out of
    /// participation, still goes to the right place.
    ///
    /// Told from participation, the one place a fleet id is known together with its server: every own pilot in a
    /// fleet with that id has to be in it on the same server. The same id on two servers, or on a client-only fleet,
    /// answers nothing — a fleet id alone is only unique per server, and a wrong server would hand a pilot's run to an
    /// operator they never flew for.
    /// </summary>
    private async Task<Dictionary<string, string?>> _FleetServersAsync(IServiceProvider services,
        string[] groupCodes, CancellationToken cancellationToken)
    {
        List<RunGroupOrigin> origins;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
            origins = await db.Set<RunGroupOrigin>().AsNoTracking()
                .Where(origin => groupCodes.Contains(origin.GroupCode))
                .ToListAsync(cancellationToken);
        Dictionary<string, string?> servers = new(StringComparer.Ordinal);
        foreach (RunGroupOrigin origin in origins)
        {
            string? serverAddress = origin.ServerAddress ?? _ServerOfFleet(origin.FleetId);
            if (origin.ServerAddress is null && serverAddress is not null)
                await services.GetRequiredService<IDispatcher>()
                    .Send(new RecordRunGroupServerCommand(origin.GroupCode, serverAddress), cancellationToken);
            servers[origin.GroupCode] = serverAddress;
        }

        return servers;
    }

    private string? _ServerOfFleet(long fleetId)
    {
        FleetParticipant[] inFleet = [.. _participation.Current.Where(participant => participant.FleetId == fleetId)];
        if (inFleet.Length == 0 || inFleet.Any(participant => participant.ClientOnly))
            return null;

        string?[] servers = [.. inFleet.Select(participant => participant.ServerAddress).Distinct()];
        return servers.Length == 1 ? servers[0] : null;
    }

    private static async Task<bool> _IsEnabledAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        foreach (ClientSetting setting in await services.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken))
            if (setting.Key == EnabledSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }
}
