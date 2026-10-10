using System.Text.Json;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Market.Services.Implementations;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Runs;

/// <summary>
/// Publishes a pilot's finished runs to EVE Workbench the moment they are saved, corrected or deleted (ET-325), when that
/// pilot switched it on. Same shape as <see cref="FleetRunAutoPublisher"/>: a local change event, one piece of work at a
/// time, nothing a SAVE waits on.
///
/// <para><b>The queue is the store.</b> The last answer per run lives in one setting (<see cref="StateSettingKey"/>), keyed
/// by run and revision. A run is marked pending before anything is sent, so a failed call or a restart only means it is
/// sent again: on startup and after a backoff every pending or failed entry is retried.</para>
/// </summary>
public sealed class EveWorkbenchRunAutoPublisher : ISingletonService, IDisposable
{
    public const string StateSettingKey = "runs.ewb-publish.state";

    /// <summary>Optional override of the EVE Workbench address; the live API without it.</summary>
    public const string UrlSettingKey = "runs.ewb-publish.url";

    /// <summary>"true" lets every run saved after <see cref="AutoUploadSinceKey"/> go by itself; off without a row.</summary>
    public const string AutoUploadKey = "runs.ewb-publish.auto";

    public const string AutoUploadSinceKey = "runs.ewb-publish.auto-since";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly EveWorkbenchKeyRing _keyRing;
    private readonly EveWorkbenchKeyStore _legacyKeyStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RunChangeFeed _feed;
    private readonly ILogger<EveWorkbenchRunAutoPublisher> _logger;
    private readonly IDisposable _runsChanged;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly HashSet<Guid> _changedRunIds = [];
    private Task _work = Task.CompletedTask;
    private TimeSpan _backoff = FirstBackoff;
    private bool _retryScheduled;

    public EveWorkbenchRunAutoPublisher(IEventBus eventBus, IServiceScopeFactory scopeFactory,
        IDbContextFactory<ClientDbContext> contextFactory, EveWorkbenchKeyRing keyRing, EveWorkbenchKeyStore legacyKeyStore,
        IHttpClientFactory httpClientFactory, RunChangeFeed feed, ILogger<EveWorkbenchRunAutoPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _contextFactory = contextFactory;
        _keyRing = keyRing;
        _legacyKeyStore = legacyKeyStore;
        _httpClientFactory = httpClientFactory;
        _feed = feed;
        _logger = logger;
        _runsChanged = eventBus.Subscribe<RunsChangedEvent>(_OnRunsChangedAsync);
        _ = _Enqueue(_StartAsync);
    }

    /// <summary>Completes once nothing is left to do — for a test to wait on.</summary>
    public async Task WhenIdleAsync()
    {
        Task current;
        do
        {
            lock (_gate)
            {
                current = _work;
            }

            await current;
        } while (!_IsLast(current));
    }

    /// <summary>The runs of one activity that are the user's own and saved: what the upload dialog offers.</summary>
    public async Task<IReadOnlyList<UploadableRun>> FindUploadableRunsAsync(string? groupCode, Guid? runId,
        IReadOnlySet<long> ownCharacterIds, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().AsNoTracking()
            .Where(run => (groupCode != null ? run.GroupCode == groupCode : run.Id == runId)
                && run.State == RunState.Saved && run.DeletedAtUtc == null && ownCharacterIds.Contains(run.CharacterId))
            .Select(run => new UploadableRun(run.Id, run.CharacterId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The deliberate act: these runs are uploaded now and stay in step from here on (new revision, delete,
    /// discard). A run never passed here is never sent.</summary>
    public async Task UploadAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken = default)
    {
        List<Run> runs;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            runs = await db.Set<Run>().AsNoTracking().Where(run => runIds.Contains(run.Id)).ToListAsync(cancellationToken);
        }

        await _UpdateStateAsync(state => _Record(state, runs, "Pending", null), cancellationToken);
        _Announce(runs);

        _ = _Enqueue(token => _PublishAsync([.. runIds], token));
    }

    /// <summary>The small label of an activity's runs: null when none was ever uploaded, else the worst thing standing.</summary>
    public async Task<string?> StatusLabelAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
        PublishEntry[] mine = [.. runIds.Select(id => state.GetValueOrDefault(id)).OfType<PublishEntry>()];
        if (mine.Length == 0)
        {
            return null;
        }

        PublishEntry? rejected = mine.FirstOrDefault(entry => entry.Status == "Rejected");
        return rejected is not null ? $"EWB ✗ rejected: {rejected.Reason}"
            : mine.Any(entry => entry.Status == "KeyInvalid") ? "EWB ✗ API key invalid"
            : mine.Any(entry => entry.Status == "NoKey") ? "EWB … needs an API key"
            : mine.Any(entry => !entry.IsAnswered) ? "EWB … pending, retrying"
            : "EWB ✓ uploaded";
    }

    public void Dispose() => _runsChanged.Dispose();

    private bool _IsLast(Task work)
    {
        lock (_gate)
        {
            return ReferenceEquals(work, _work);
        }
    }

    private Task _OnRunsChangedAsync(RunsChangedEvent changed, CancellationToken cancellationToken)
    {
        if (changed.Data.RunId is not { } runId)
        {
            return Task.CompletedTask;
        }

        bool firstOfItsTurn;
        lock (_gate)
        {
            firstOfItsTurn = _changedRunIds.Count == 0;
            _changedRunIds.Add(runId);
        }

        if (firstOfItsTurn)
        {
            _ = _Enqueue(_PublishChangedAsync);
        }

        return Task.CompletedTask;
    }

    private Task _Enqueue(Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            return _work = _work.ContinueWith(_ => _RunAsync(work), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
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
            _logger.LogError(exception, "Publishing runs to EVE Workbench failed");
        }
    }

    private Task _PublishChangedAsync(CancellationToken cancellationToken)
    {
        Guid[] runIds;
        lock (_gate)
        {
            runIds = [.. _changedRunIds];
            _changedRunIds.Clear();
        }

        return _PublishAsync(runIds, cancellationToken);
    }

    private async Task _StartAsync(CancellationToken cancellationToken)
    {
        await _keyRing.MigrateLegacyAsync(await _legacyKeyStore.GetTokenAsync(cancellationToken), cancellationToken);
        await _RetryAsync(cancellationToken);
    }

    private async Task _RetryAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _retryScheduled = false;
        }

        Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
        await _PublishAsync([.. state.Where(entry => !entry.Value.IsAnswered).Select(entry => entry.Key)], cancellationToken);
    }

    private async Task _PublishAsync(Guid[] runIds, CancellationToken cancellationToken)
    {
        if (runIds.Length == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IReadOnlyList<ClientSetting> settings = await scope.ServiceProvider.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken);
        Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
        runIds = await _EligibleAsync(runIds, state, settings, cancellationToken);
        if (runIds.Length == 0)
        {
            return;
        }

        RunSynchronizationService synchronization = scope.ServiceProvider.GetRequiredService<RunSynchronizationService>();
        List<Run> runs;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            runs = await RunSynchronizationService.IncludeGraph(db.Set<Run>().AsNoTracking()
                    .Where(run => runIds.Contains(run.Id)
                        && (run.State == RunState.Saved || run.DeletedAtUtc != null)))
                .ToListAsync(cancellationToken);
        }

        runs = [.. runs.Where(run => !(state.GetValueOrDefault(run.Id) is { IsAnswered: true } known && known.Revision == run.Revision))];
        if (runs.Count == 0)
        {
            return;
        }

        string baseUrl = settings.FirstOrDefault(setting => setting.Key == UrlSettingKey)?.Value ?? EveWorkbenchFitClient.BaseUrl;
        var publisher = new EveWorkbenchRunPublisher(_httpClientFactory.CreateClient(EveWorkbenchRunPublisher.HttpClientName),
            new LogAdapter(_logger));
        bool anyFailed = false;
        Dictionary<Guid, PublishEntry> changes = [];
        // One request series per key: pilots of one EVE Workbench account share a key, and a refused key hits only them.
        List<(ResolvedEveWorkbenchKey? Key, List<Run> Runs)> groups = [];
        foreach (Run run in runs)
        {
            ResolvedEveWorkbenchKey? key = await _keyRing.ForPilotAsync(run.CharacterId, cancellationToken);
            int index = groups.FindIndex(group => group.Key?.MainId == key?.MainId);
            if (index < 0)
            {
                groups.Add((key, [run]));
            }
            else
            {
                groups[index].Runs.Add(run);
            }
        }

        foreach ((ResolvedEveWorkbenchKey? key, List<Run> keyRuns) in groups)
        {
            if (key is null || key.Invalid)
            {
                _Record(changes, keyRuns, key is null ? "NoKey" : "KeyInvalid", key is null ? "no API key" : "API key refused");
                continue;
            }

            await _UpdateStateAsync(current => _Record(current, keyRuns, "Pending", null), cancellationToken);
            IReadOnlyList<RunWirePayload> payloads = await synchronization.BuildPayloadsAsync(keyRuns, cancellationToken);
            EveWorkbenchRunPublishOutcome outcome = await publisher.PublishAsync(true, baseUrl, key.Token, payloads, cancellationToken);
            foreach (EveWorkbenchRunImportResult result in outcome.Results)
            {
                if (keyRuns.FirstOrDefault(run => run.Id == result.ExternalId) is { } run)
                {
                    changes[run.Id] = new PublishEntry(run.CharacterId, run.Revision, result.Status.ToString(), result.Reason, DateTime.UtcNow);
                    if (result.Status == EveWorkbenchRunImportStatus.Rejected)
                    {
                        _logger.LogWarning("EVE Workbench rejected run {RunId}: {Reason}", run.Id, result.Reason);
                    }
                }
            }

            _Record(changes, [.. keyRuns.Where(run => outcome.Pending.Any(payload => payload.Run.Id == run.Id))],
                outcome.Unauthorized ? "KeyInvalid" : "Failed", outcome.Unauthorized ? "API key refused" : "EVE Workbench did not answer");
            if (outcome.Unauthorized)
            {
                await _keyRing.MarkInvalidAsync(key.MainId, cancellationToken);
            }

            // A refused key is not retried on a timer: it waits for a new key.
            anyFailed |= outcome.Pending.Count > 0 && !outcome.Unauthorized;
        }

        // Merged into a fresh copy: an upload may have marked other runs pending while the HTTP calls were out.
        await _UpdateStateAsync(current =>
        {
            foreach ((Guid id, PublishEntry entry) in changes)
            {
                current[id] = entry;
            }
        }, cancellationToken);
        _ScheduleRetryIfNeeded(anyFailed);
        _Announce(runs);
    }

    public async Task<bool> IsAutoUploadAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IReadOnlyList<ClientSetting> settings = await scope.ServiceProvider.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken);
        return settings.FirstOrDefault(setting => setting.Key == AutoUploadKey)?.Value == "true";
    }

    /// <summary>Switching it on starts the clock: only runs saved from then on go by themselves, never the old ones.</summary>
    public async Task SetAutoUploadAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled == await IsAutoUploadAsync(cancellationToken))
        {
            return;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ISettingRepository settings = scope.ServiceProvider.GetRequiredService<ISettingRepository>();
        if (enabled)
        {
            await settings.UpsertAsync(AutoUploadSinceKey, DateTime.UtcNow.ToString("O"), cancellationToken);
        }

        await settings.UpsertAsync(AutoUploadKey, enabled ? "true" : "false", cancellationToken);
    }

    /// <summary>What is kept in step: runs that were uploaded, plus — with auto-upload on — runs saved since it was
    /// switched on by a pilot with a usable key. A pilot without one is skipped silently, with no chip.</summary>
    private async Task<Guid[]> _EligibleAsync(Guid[] runIds, Dictionary<Guid, PublishEntry> state,
        IReadOnlyList<ClientSetting> settings, CancellationToken cancellationToken)
    {
        List<Guid> eligible = [.. runIds.Where(state.ContainsKey)];
        if (settings.FirstOrDefault(setting => setting.Key == AutoUploadKey)?.Value != "true"
            || !DateTime.TryParse(settings.FirstOrDefault(setting => setting.Key == AutoUploadSinceKey)?.Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime since))
        {
            return [.. eligible];
        }

        Guid[] fresh = [.. runIds.Where(id => !state.ContainsKey(id))];
        List<(Guid Id, long CharacterId)> candidates;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            candidates = [.. (await db.Set<Run>().AsNoTracking()
                    .Where(run => fresh.Contains(run.Id) && run.State == RunState.Saved && run.DeletedAtUtc == null && run.SavedAtUtc >= since)
                    .Select(run => new { run.Id, run.CharacterId })
                    .ToListAsync(cancellationToken)).Select(run => (run.Id, run.CharacterId))];
        }

        foreach ((Guid id, long characterId) in candidates)
        {
            if (await _keyRing.ForPilotAsync(characterId, cancellationToken) is { Invalid: false })
            {
                eligible.Add(id);
            }
        }

        return [.. eligible];
    }

    /// <summary>Stores a key entered in a pilot's dialog; the runs waiting for a key go out at once.</summary>
    public async Task<EveWorkbenchKeyCheck> SaveKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        EveWorkbenchKeyCheck check = await _keyRing.AddAsync(key, cancellationToken);
        if (check.Verdict == EveWorkbenchKeyVerdict.Valid)
        {
            _ = _Enqueue(_RetryAsync);
        }

        return check;
    }

    /// <summary>The key a pilot publishes with, after asking EVE Workbench who each stored key still covers.</summary>
    public async Task<ResolvedEveWorkbenchKey?> KeyForPilotAsync(long characterId, bool refresh, CancellationToken cancellationToken = default)
    {
        if (refresh)
        {
            await _keyRing.RefreshAsync(cancellationToken);
        }

        return await _keyRing.ForPilotAsync(characterId, cancellationToken);
    }

    public Task ClearKeyAsync(long mainId, CancellationToken cancellationToken = default) => _keyRing.RemoveAsync(mainId, cancellationToken);

    /// <summary>Tells the runs screens where an upload stands now, after every attempt: not on the bus, so it never wakes this publisher.</summary>
    private void _Announce(IEnumerable<Run> runs)
    {
        foreach (Run run in runs)
        {
            _feed.Announce(run.Id, run.GroupCode);
        }
    }

    private static void _Record(Dictionary<Guid, PublishEntry> state, IEnumerable<Run> runs, string status, string? reason)
    {
        foreach (Run run in runs)
        {
            state[run.Id] = new PublishEntry(run.CharacterId, run.Revision, status, reason, DateTime.UtcNow);
        }
    }

    private void _ScheduleRetryIfNeeded(bool failed)
    {
        lock (_gate)
        {
            if (!failed)
            {
                _backoff = FirstBackoff;
                return;
            }

            if (_retryScheduled)
            {
                return;
            }

            _retryScheduled = true;
            TimeSpan delay = _backoff;
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
            _ = Task.Delay(delay).ContinueWith(_ => _Enqueue(_RetryAsync), CancellationToken.None);
        }
    }

    private async Task<Dictionary<Guid, PublishEntry>> _LoadStateAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IReadOnlyList<ClientSetting> settings = await scope.ServiceProvider.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken);
        string? json = settings.FirstOrDefault(setting => setting.Key == StateSettingKey)?.Value;
        return string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<Guid, PublishEntry>>(json) ?? [];
    }

    /// <summary>Every change of the state is one load-change-save under this lock, so no writer overwrites another's.</summary>
    private async Task _UpdateStateAsync(Action<Dictionary<Guid, PublishEntry>> change, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
            change(state);
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            await _SaveStateAsync(scope.ServiceProvider, state, cancellationToken);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static Task _SaveStateAsync(IServiceProvider services, Dictionary<Guid, PublishEntry> state, CancellationToken cancellationToken) =>
        services.GetRequiredService<ISettingRepository>().UpsertAsync(StateSettingKey, JsonSerializer.Serialize(state), cancellationToken);

    /// <summary>The last answer for a run at a revision; Pending, Failed, NoKey and KeyInvalid are still to be sent.</summary>
    private sealed record PublishEntry(long CharacterId, int Revision, string Status, string? Reason, DateTime AtUtc)
    {
        public bool IsAnswered => Status is not ("Pending" or "Failed" or "NoKey" or "KeyInvalid");
    }

    private sealed class LogAdapter(ILogger logger) : IEveWorkbenchRunPublishLogger
    {
        public void Log(string message) => logger.LogInformation("{Message}", message);
    }
}

public sealed record UploadableRun(Guid RunId, long CharacterId);
