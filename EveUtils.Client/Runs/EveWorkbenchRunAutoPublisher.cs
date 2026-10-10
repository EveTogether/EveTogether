using System.Text.Json;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Market.Services;
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
    /// <summary>Per pilot: <see cref="EnabledSettingKeyFor"/> holds "true" once the pilot chose to share. Off without a row.</summary>
    public const string EnabledSettingKeyPrefix = "runs.ewb-publish.";

    public const string StateSettingKey = "runs.ewb-publish.state";

    /// <summary>Optional override of the EVE Workbench address; the live API without it.</summary>
    public const string UrlSettingKey = "runs.ewb-publish.url";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDbContextFactory<ClientDbContext> _contextFactory;
    private readonly IEveWorkbenchKeyStore _keyStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EveWorkbenchRunAutoPublisher> _logger;
    private readonly IDisposable _runsChanged;
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _changedRunIds = [];
    private Task _work = Task.CompletedTask;
    private TimeSpan _backoff = FirstBackoff;
    private bool _retryScheduled;

    public EveWorkbenchRunAutoPublisher(IEventBus eventBus, IServiceScopeFactory scopeFactory,
        IDbContextFactory<ClientDbContext> contextFactory, IEveWorkbenchKeyStore keyStore,
        IHttpClientFactory httpClientFactory, ILogger<EveWorkbenchRunAutoPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _contextFactory = contextFactory;
        _keyStore = keyStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _runsChanged = eventBus.Subscribe<RunsChangedEvent>(_OnRunsChangedAsync);
        _ = _Enqueue(_RetryAsync);
    }

    public static string EnabledSettingKeyFor(long characterId) => EnabledSettingKeyPrefix + characterId;

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

    /// <summary>What the pilot sees next to the switch: how many runs went, and the newest failure.</summary>
    public async Task<string> StatusLineAsync(long characterId, CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
        PublishEntry[] mine = [.. state.Values.Where(entry => entry.CharacterId == characterId)];
        if (mine.Length == 0)
        {
            return "Nothing sent yet.";
        }

        int sent = mine.Count(entry => entry.Status is "Created" or "Updated" or "Ignored");
        int waiting = mine.Count(entry => !entry.IsAnswered);
        PublishEntry latest = mine.OrderByDescending(entry => entry.AtUtc).First();
        string headline = latest.Status switch
        {
            "NoKey" => $"Waiting for an API key: {waiting} run(s) not sent yet.",
            "KeyInvalid" => $"API-key ongeldig — stel opnieuw in. {waiting} run(s) wachten.",
            "Pending" or "Failed" => $"Last upload failed ({latest.Reason ?? "no answer"}); retrying. {waiting} run(s) waiting.",
            "Rejected" => $"Last upload was rejected: {latest.Reason}.",
            _ => "Last upload succeeded."
        };
        return $"{headline} {sent} run(s) sent in total, last answer {latest.AtUtc.ToLocalTime():g}.";
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
        HashSet<long> enabledPilots = [.. settings
            .Where(setting => setting.Key.StartsWith(EnabledSettingKeyPrefix, StringComparison.Ordinal) && setting.Value == "true"
                && long.TryParse(setting.Key.AsSpan(EnabledSettingKeyPrefix.Length), out _))
            .Select(setting => long.Parse(setting.Key[EnabledSettingKeyPrefix.Length..]))];
        if (enabledPilots.Count == 0)
        {
            return;
        }

        Dictionary<Guid, PublishEntry> state = await _LoadStateAsync(cancellationToken);
        RunSynchronizationService synchronization = scope.ServiceProvider.GetRequiredService<RunSynchronizationService>();
        List<Run> runs;
        await using (ClientDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            runs = await RunSynchronizationService.IncludeGraph(db.Set<Run>().AsNoTracking()
                    .Where(run => runIds.Contains(run.Id) && enabledPilots.Contains(run.CharacterId)
                        && (run.State == RunState.Saved || run.DeletedAtUtc != null)))
                .ToListAsync(cancellationToken);
        }

        runs = [.. runs.Where(run => !(state.GetValueOrDefault(run.Id) is { IsAnswered: true } known && known.Revision == run.Revision))];
        if (runs.Count == 0)
        {
            return;
        }

        string? token = await _keyStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            _Record(state, runs, "NoKey", "no API key");
            await _SaveStateAsync(scope.ServiceProvider, state, cancellationToken);
            return;
        }

        _Record(state, runs, "Pending", null);
        await _SaveStateAsync(scope.ServiceProvider, state, cancellationToken);

        string baseUrl = settings.FirstOrDefault(setting => setting.Key == UrlSettingKey)?.Value ?? EveWorkbenchFitClient.BaseUrl;
        IReadOnlyList<RunWirePayload> payloads = await synchronization.BuildPayloadsAsync(runs, cancellationToken);
        var publisher = new EveWorkbenchRunPublisher(_httpClientFactory.CreateClient(EveWorkbenchRunPublisher.HttpClientName),
            new LogAdapter(_logger));
        EveWorkbenchRunPublishOutcome outcome = await publisher.PublishAsync(true, baseUrl, token, payloads, cancellationToken);

        foreach (EveWorkbenchRunImportResult result in outcome.Results)
        {
            if (runs.FirstOrDefault(run => run.Id == result.ExternalId) is { } run)
            {
                state[run.Id] = new PublishEntry(run.CharacterId, run.Revision, result.Status.ToString(), result.Reason, DateTime.UtcNow);
                if (result.Status == EveWorkbenchRunImportStatus.Rejected)
                {
                    _logger.LogWarning("EVE Workbench rejected run {RunId}: {Reason}", run.Id, result.Reason);
                }
            }
        }

        _Record(state, [.. runs.Where(run => outcome.Pending.Any(payload => payload.Run.Id == run.Id))],
            outcome.Unauthorized ? "KeyInvalid" : "Failed", outcome.Unauthorized ? "API key refused" : "EVE Workbench did not answer");
        await _SaveStateAsync(scope.ServiceProvider, state, cancellationToken);
        // A refused key is not retried on a timer: it waits for a new key (see SaveKeyAsync).
        _ScheduleRetryIfNeeded(outcome.Pending.Count > 0 && !outcome.Unauthorized);
    }

    public async Task<bool> HasKeyAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await _keyStore.GetTokenAsync(cancellationToken));

    /// <summary>Asks EVE Workbench which characters the stored key covers; null without a key.</summary>
    public async Task<EveWorkbenchKeyCheck?> CheckStoredKeyAsync(CancellationToken cancellationToken = default) =>
        await _keyStore.GetTokenAsync(cancellationToken) is { Length: > 0 } token ? await _CheckAsync(token, cancellationToken) : null;

    /// <summary>Checks <paramref name="key"/> first and stores it unless EVE Workbench refuses it; a stored key sets
    /// the waiting runs going again at once. One key for appraisal and upload alike.</summary>
    public async Task<EveWorkbenchKeyCheck> SaveKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        string trimmed = key.Trim();
        EveWorkbenchKeyCheck check = await _CheckAsync(trimmed, cancellationToken);
        if (check.Verdict != EveWorkbenchKeyVerdict.Invalid)
        {
            await _keyStore.SetTokenAsync(trimmed, cancellationToken);
            _ = _Enqueue(_RetryAsync);
        }

        return check;
    }

    public Task ClearKeyAsync(CancellationToken cancellationToken = default) => _keyStore.SetTokenAsync(null, cancellationToken);

    private async Task<EveWorkbenchKeyCheck> _CheckAsync(string token, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IReadOnlyList<ClientSetting> settings = await scope.ServiceProvider.GetRequiredService<ISettingRepository>().ListAsync(cancellationToken);
        string baseUrl = settings.FirstOrDefault(setting => setting.Key == UrlSettingKey)?.Value ?? EveWorkbenchFitClient.BaseUrl;
        var publisher = new EveWorkbenchRunPublisher(_httpClientFactory.CreateClient(EveWorkbenchRunPublisher.HttpClientName));
        return await publisher.CheckKeyAsync(baseUrl, token, cancellationToken);
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
