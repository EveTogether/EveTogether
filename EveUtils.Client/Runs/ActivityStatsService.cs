using System.Collections.Concurrent;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>
/// The figures behind the runs overview's BY ACTIVITY list (ET-517). One read per tab refresh, not one per row: the
/// own runs come from two queries however many activities there are, and a server is asked which activities it holds
/// figures for before any figure is asked of it, so a site nobody else flew costs no call at all. A figure that is read
/// stays for <see cref="CacheFor"/>; the list is read again on every change to the runs, and a read inside that
/// window answers from here.
///
/// <para>Reading a server is part of opting in, like the run window's panel (ET-516): a pilot who shares nothing gets
/// the own runs and is told so. A server that cannot answer is a reason on the row, never an error and never a toast.</para>
/// </summary>
public sealed class ActivityStatsService(IServiceScopeFactory scopeFactory, IExpectedEarningsSource source,
    IServerRunOutcomeClient client, IRemoteBusConnector connector, RunOutcomeSharingChoices choices, ISharedServerPicks picks)
    : IActivityStatsSource, ISingletonService
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private const int ParallelServerReads = 4;
    private static readonly TimeSpan ListedWindow = EarningsPolicy.Default.FallbackWindow ?? EarningsPolicy.Default.Window;

    private readonly ConcurrentDictionary<string, Cached<IReadOnlyList<RunOutcomeKeyCountDto>>> _listed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Cached<SharedEarningsAttempt>> _figures = new(StringComparer.Ordinal);

    public async Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default) =>
        request.ServerAddress is { } server
            ? await _ReadServerTabAsync(request, server, cancellationToken)
            : await _ReadLocalTabAsync(request, cancellationToken);

    public async Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds,
        CancellationToken cancellationToken = default)
    {
        ExpectedEarnings own = await source.LoadOwnAsync(key, cancellationToken);
        (int characterId, string[] servers, OwnRunsReason reason, _) = await _ReaderAsync(characterIds, null, cancellationToken);
        SharedEarningsAttempt attempt = servers.Length == 0
            ? new SharedEarningsAttempt(null, reason)
            : (await _FigureAsync(key, characterId, null, cancellationToken)).Attempt;
        return new ActivityStats(key, null, null, null, attempt.Shared ?? own, own,
            attempt.Shared is null ? attempt.Reason : OwnRunsReason.None, attempt.Answers,
            (attempt.Shared ?? own).Figures.EffectiveRunCount, DateTime.UtcNow);
    }

    private async Task<ActivityStatsOverview> _ReadLocalTabAsync(ActivityStatsRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<ActivityEarningsDto> inRange;
        Dictionary<ActivityStatsKey, ActivityEarningsDto> recent;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            inRange = (await dispatcher.Query(new GetActivityEarningsQuery(FromUtc: request.FromUtc, ToUtc: request.ToUtc), cancellationToken)).Value ?? [];
            recent = ((await dispatcher.Query(new GetActivityEarningsQuery(), cancellationToken)).Value ?? [])
                .ToDictionary(activity => activity.Key);
        }

        if (inRange.Count == 0)
            return new ActivityStatsOverview([]);

        SharedReads shared = await _ReadSharedAsync([.. inRange.Select(activity => activity.Key)], request.CharacterIds, null, cancellationToken);
        return new ActivityStatsOverview([.. inRange.Select(activity =>
        {
            // An activity older than the statistic window has no recent figure of its own: the range's stands in.
            EarningsFiguresDto ownFigures = recent.TryGetValue(activity.Key, out ActivityEarningsDto? known) ? known.Figures : activity.Figures;
            ExpectedEarnings own = new(EarningsBasis.YourRuns, ownFigures, ownFigures.PilotCount);
            (SharedEarningsAttempt attempt, DateTime readAtUtc) = shared.Figures.TryGetValue(activity.Key, out var figure)
                ? (figure.Attempt, figure.AtUtc)
                : (new SharedEarningsAttempt(null, shared.MissingReason), shared.ReadAtUtc);
            return new ActivityStats(activity.Key, activity.SiteName, activity.LastFlownUtc, activity.Figures,
                attempt.Shared ?? own, own, attempt.Shared is null ? attempt.Reason : OwnRunsReason.None, attempt.Answers,
                attempt.Shared?.Figures.EffectiveRunCount ?? ownFigures.EffectiveRunCount, readAtUtc);
        })]);
    }

    private async Task<ActivityStatsOverview> _ReadServerTabAsync(ActivityStatsRequest request, string server, CancellationToken cancellationToken)
    {
        SharedReads shared = await _ReadSharedAsync(null, request.CharacterIds, server, cancellationToken);
        if (shared.Message is not null)
            return new ActivityStatsOverview([], shared.Message);

        return new ActivityStatsOverview([.. shared.Listed.Select(listed =>
        {
            if (!shared.Figures.TryGetValue(listed.Key, out var figure))
                return new ActivityStats(listed.Key, null, null, null, null, null, OwnRunsReason.ServerUnavailable, [],
                    listed.EffectiveRunCount, shared.ReadAtUtc);

            return new ActivityStats(listed.Key, null, null, null, figure.Attempt.Shared, null,
                figure.Attempt.Shared is null ? figure.Attempt.Reason : OwnRunsReason.None, figure.Attempt.Answers,
                listed.EffectiveRunCount, figure.AtUtc);
        })]);
    }

    /// <param name="keys">The activities to find figures for, or null for every activity the server lists.</param>
    /// <param name="onlyServer">The server tab asked for, null for every server the pilot opted in on.</param>
    private async Task<SharedReads> _ReadSharedAsync(IReadOnlyList<ActivityStatsKey>? keys, IReadOnlyList<int> characterIds,
        string? onlyServer, CancellationToken cancellationToken)
    {
        DateTime readAtUtc = DateTime.UtcNow;
        (int characterId, string[] servers, OwnRunsReason reason, string? message) = await _ReaderAsync(characterIds, onlyServer, cancellationToken);
        if (servers.Length == 0)
            return new SharedReads(new Dictionary<ActivityStatsKey, KeyFigure>(), [], reason, message, readAtUtc);

        List<RunOutcomeKeyCountDto> listed = [];
        bool anyListed = false;
        foreach (string server in servers)
        {
            if (await _ListAsync(server, characterId, cancellationToken) is not { } keysOfServer)
                continue;

            anyListed = true;
            listed.AddRange(keysOfServer);
        }

        if (!anyListed)
            return new SharedReads(new Dictionary<ActivityStatsKey, KeyFigure>(), [], OwnRunsReason.ServerUnavailable,
                onlyServer is null ? null : "The server could not be reached.", readAtUtc);

        RunOutcomeKeyCountDto[] wanted = [.. listed
            .GroupBy(count => count.Key)
            .Where(group => keys is null || keys.Contains(group.Key))
            .Select(group => group.OrderByDescending(count => count.EffectiveRunCount).First())];
        ConcurrentDictionary<ActivityStatsKey, KeyFigure> figures = new();
        await Parallel.ForEachAsync(wanted,
            new ParallelOptions { MaxDegreeOfParallelism = ParallelServerReads, CancellationToken = cancellationToken },
            async (count, token) => figures[count.Key] = await _FigureAsync(count.Key, characterId, onlyServer, token));

        // A key no server lists has fewer than the minimum of pilots on every server: that is the reason, not "unavailable".
        return new SharedReads(figures, wanted, OwnRunsReason.TooFewPilots, null, readAtUtc);
    }

    /// <summary>The pilot's first character that is coupled to, opted in on and connected to a server (the one asked, on a
    /// server tab), with the servers that qualifies it for. Otherwise the reason there is none.</summary>
    private async Task<(int CharacterId, string[] Servers, OwnRunsReason Reason, string? Message)> _ReaderAsync(
        IReadOnlyList<int> characterIds, string? onlyServer, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IClientSessionStore store = scope.ServiceProvider.GetRequiredService<IClientSessionStore>();
        IReadOnlyDictionary<string, OutcomeSharingChoice> chosen = await choices.LoadAsync(cancellationToken);
        OwnRunsReason reason = OwnRunsReason.None;
        string? message = null;
        foreach (int characterId in characterIds.Order())
        {
            string[] coupled = [.. (await store.ListServersForCharacterAsync(characterId, cancellationToken))
                .Where(server => onlyServer is null || server == onlyServer)];
            if (coupled.Length == 0)
                continue;

            string[] sharing = [.. coupled.Where(server => chosen.GetValueOrDefault(server) is OutcomeSharingChoice.Shared).Order(StringComparer.Ordinal)];
            if (sharing.Length == 0)
            {
                reason = OwnRunsReason.NotSharing;
                message ??= "Turn on sharing in Settings to see what this server's pilots earn.";
                continue;
            }

            string[] connected = [.. sharing.Where(server => connector.StateFor(server, characterId) == ServerConnectionState.Connected)];
            if (connected.Length > 0)
                return (characterId, connected, OwnRunsReason.None, null);

            if (reason is not OwnRunsReason.NotSharing)
                reason = OwnRunsReason.ServerUnavailable;
            message = "Not connected to this server.";
        }

        return (0, [], reason, onlyServer is null ? null : message ?? "None of your characters is coupled to this server.");
    }

    private async Task<IReadOnlyList<RunOutcomeKeyCountDto>?> _ListAsync(string server, int characterId, CancellationToken cancellationToken)
    {
        if (_listed.TryGetValue(server, out Cached<IReadOnlyList<RunOutcomeKeyCountDto>>? cached) && cached.IsFresh)
            return cached.Value;

        OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>> read = await client.ListOutcomeKeysAsync(server, characterId,
            DateOnly.FromDateTime(DateTime.UtcNow - ListedWindow), cancellationToken);
        if (read.Value is not { } keys)
            return null;

        _listed[server] = new Cached<IReadOnlyList<RunOutcomeKeyCountDto>>(keys);
        return keys;
    }

    private async Task<KeyFigure> _FigureAsync(ActivityStatsKey key, int characterId, string? onlyServer, CancellationToken cancellationToken)
    {
        string cacheKey = $"{key.Code}|{onlyServer}";
        if (_figures.TryGetValue(cacheKey, out Cached<SharedEarningsAttempt>? cached) && cached.IsFresh)
            return new KeyFigure(await _WithPickAsync(cached.Value, onlyServer, cancellationToken), cached.AtUtc);

        SharedEarningsAttempt attempt = await source.LoadSharedAsync(key, characterId, cancellationToken, onlyServer: onlyServer);
        var figure = new Cached<SharedEarningsAttempt>(attempt);
        // A server that could not be asked is worth asking again next time; only an answer is kept.
        if (attempt.Reason is not OwnRunsReason.ServerUnavailable)
            _figures[cacheKey] = figure;
        return new KeyFigure(attempt, figure.AtUtc);
    }

    /// <summary>The server the pilot picked since the figure was read still counts: the answers of every server are kept, so
    /// switching is a choice among them and never a new read.</summary>
    private async Task<SharedEarningsAttempt> _WithPickAsync(SharedEarningsAttempt attempt, string? onlyServer,
        CancellationToken cancellationToken)
    {
        if (onlyServer is not null || attempt.Answers.Count < 2)
            return attempt;

        return new SharedEarningsAttempt(SharedServerChoice.Pick(attempt.Answers, null, await picks.LoadAsync(cancellationToken)),
            OwnRunsReason.None, attempt.Answers);
    }

    private sealed record KeyFigure(SharedEarningsAttempt Attempt, DateTime AtUtc);

    private sealed record SharedReads(IReadOnlyDictionary<ActivityStatsKey, KeyFigure> Figures,
        IReadOnlyList<RunOutcomeKeyCountDto> Listed, OwnRunsReason MissingReason, string? Message, DateTime ReadAtUtc);

    private sealed class Cached<T>(T value)
    {
        public DateTime AtUtc { get; } = DateTime.UtcNow;

        public T Value { get; } = value;

        public bool IsFresh => DateTime.UtcNow - AtUtc < CacheFor;
    }
}
