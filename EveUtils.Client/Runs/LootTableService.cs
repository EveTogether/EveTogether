using System.Collections.Concurrent;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>
/// The loot table's figures (ET-519). A server is read once over the long window and kept for <see cref="CacheFor"/>:
/// switching the window is a new table out of the same outcomes, never a new call. Reading a server is part of opting in
/// (ET-512), like the earnings panel: a pilot who shares nothing, or whose server has fewer than the minimum of pilots on
/// the activity, gets the own runs and is told so, never an error and never a toast.
/// </summary>
public sealed class LootTableService(IServiceScopeFactory scopeFactory, IServerRunOutcomeClient client, IRemoteBusConnector connector,
    RunOutcomeSharingChoices choices, ISharedServerPicks picks) : ILootTableSource, ISingletonService
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ReadWindow = LootTablePolicy.Default.LongWindow;

    private readonly ConcurrentDictionary<string, ServerRead> _reads = new(StringComparer.Ordinal);

    public async Task<LootTableReading> ReadAsync(LootTableRequest request, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        OwnRunsReason reason = OwnRunsReason.None;
        if (request.Basis is EarningsBasis.AllPilots)
        {
            (LootTableReading? shared, reason) = await _ReadSharedAsync(dispatcher, request, cancellationToken);
            if (shared is not null)
                return shared;
        }

        Result<LootTableDto> own = await dispatcher.Query(new GetActivityLootTableQuery(request.Key, request.Window), cancellationToken);
        LootTableDto table = own.Value ?? LootTableStatistics.Compute(request.Key, [], _ => LootRarityClass.Common, _ => null,
            LootTablePolicy.Default, request.Window, DateTime.UtcNow, null);
        return new LootTableReading(table, EarningsBasis.YourRuns, null, reason, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<LootSiteChoice>> ListFlownAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        IReadOnlyList<ActivityEarningsDto> flown = (await dispatcher.Query(new GetActivityEarningsQuery(), cancellationToken)).Value ?? [];
        return [.. flown
            .OrderByDescending(activity => activity.LastFlownUtc)
            .Select(activity => new LootSiteChoice(activity.Key, activity.SiteName, activity.Figures.RunCount, activity.LastFlownUtc))];
    }

    private async Task<(LootTableReading? Reading, OwnRunsReason Reason)> _ReadSharedAsync(IDispatcher dispatcher,
        LootTableRequest request, CancellationToken cancellationToken)
    {
        OutcomeReader reader = await OutcomeReaders.FindAsync(scopeFactory, choices, connector, request.CharacterIds,
            request.OnlyServer, cancellationToken);
        if (reader.Servers.Length == 0)
            return (null, reader.Reason);

        List<ServerRead> answers = [];
        OwnRunsReason reason = OwnRunsReason.ServerUnavailable;
        foreach (string server in reader.Servers)
        {
            ServerRead? read = await _ReadServerAsync(server, reader.CharacterId, request.Key, cancellationToken);
            if (read is null)
                continue;

            if (read.Outcomes.Availability is SharedOutcomesAvailability.Shared)
                answers.Add(read);
            else
                reason = OwnRunsReason.TooFewPilots;
        }

        if (answers.Count == 0)
            return (null, reason);

        string? pinned = request.OnlyServer is null ? await picks.LoadAsync(cancellationToken) : null;
        ServerRead chosen = answers.FirstOrDefault(answer => answer.Server == pinned)
                            ?? answers.OrderByDescending(answer => answer.Outcomes.Outcomes.Sum(outcome => outcome.Weight)).First();
        Result<LootTableDto> table = await dispatcher.Query(new GetSharedLootTableQuery(chosen.Outcomes, chosen.Loot, request.Window),
            cancellationToken);
        return table.Value is { } value
            ? (new LootTableReading(value, EarningsBasis.AllPilots, chosen.ServerName, OwnRunsReason.None, chosen.AtUtc), OwnRunsReason.None)
            : (null, OwnRunsReason.ServerUnavailable);
    }

    // A server that could not be asked is worth asking again next time: only an answer is kept.
    private async Task<ServerRead?> _ReadServerAsync(string server, int characterId, ActivityStatsKey key, CancellationToken cancellationToken)
    {
        string cacheKey = $"{server}|{key.Code}";
        if (_reads.TryGetValue(cacheKey, out ServerRead? cached) && cached.IsFresh)
            return cached;

        DateOnly since = DateOnly.FromDateTime(DateTime.UtcNow - ReadWindow);
        OutcomeReadResult<ActivityOutcomesDto> outcomes = await client.GetOutcomesAsync(server, characterId, key, since, cancellationToken);
        if (outcomes.Value is not { } read)
            return null;

        ActivityOutcomeLootDto lines;
        if (read.Availability is SharedOutcomesAvailability.Shared)
        {
            OutcomeReadResult<ActivityOutcomeLootDto> loot = await client.GetOutcomeLootAsync(server, characterId, key, since, cancellationToken);
            if (loot.Value is not { } answered)
                return null;

            lines = answered;
        }
        else
        {
            lines = new ActivityOutcomeLootDto { Availability = read.Availability, PilotCount = read.PilotCount, Outcomes = [] };
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        string name = await scope.ServiceProvider.GetRequiredService<IServerRegistry>().DisplayNameAsync(server, cancellationToken);
        return _reads[cacheKey] = new ServerRead(server, name, read, lines);
    }

    private sealed record ServerRead(string Server, string ServerName, ActivityOutcomesDto Outcomes, ActivityOutcomeLootDto Loot)
    {
        public DateTime AtUtc { get; } = DateTime.UtcNow;

        public bool IsFresh => DateTime.UtcNow - AtUtc < CacheFor;
    }
}
