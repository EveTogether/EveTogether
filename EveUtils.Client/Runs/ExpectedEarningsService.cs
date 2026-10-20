using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes;
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
/// The expected earnings of an activity for the run window (ET-516): a server's shared figure when the pilot opted in
/// and enough pilots share, else the pilot's own runs. Reading the server is part of opting in (ET-512): a pilot who
/// shares nothing gets the own runs and is told so.
/// </summary>
public sealed class ExpectedEarningsService(IServiceScopeFactory scopeFactory, IServerRunOutcomeClient client,
    IRemoteBusConnector connector, RunOutcomeSharingChoices choices) : IExpectedEarningsSource, ISingletonService
{
    private static readonly TimeSpan RecentWindow = EarningsPolicy.Default.Window;
    private static readonly TimeSpan ExtendedWindow = EarningsPolicy.Default.FallbackWindow ?? RecentWindow;

    public async Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        IReadOnlyList<ActivityEarningsDto> own = (await dispatcher.Query(new GetActivityEarningsQuery(key), cancellationToken)).Value ?? [];
        EarningsFiguresDto figures = own.FirstOrDefault(activity => activity.Key == key)?.Figures
                                     ?? EarningsStatistics.Compute([], _ => null, EarningsPolicy.For(key.Kind), DateTime.UtcNow);
        return new ExpectedEarnings(EarningsBasis.YourRuns, figures, figures.PilotCount);
    }

    public async Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        string[] coupled = [.. await services.GetRequiredService<IClientSessionStore>().ListServersForCharacterAsync(characterId, cancellationToken)];
        if (coupled.Length == 0)
            return new SharedEarningsAttempt(null, OwnRunsReason.None);

        IReadOnlyDictionary<string, OutcomeSharingChoice> chosen = await choices.LoadAsync(cancellationToken);
        string[] sharing = [.. coupled.Where(server => chosen.GetValueOrDefault(server) is OutcomeSharingChoice.Shared).Order(StringComparer.Ordinal)];
        if (sharing.Length == 0)
            return new SharedEarningsAttempt(null, OwnRunsReason.NotSharing);

        IDispatcher dispatcher = services.GetRequiredService<IDispatcher>();
        IServerRegistry registry = services.GetRequiredService<IServerRegistry>();
        OwnRunsReason reason = OwnRunsReason.ServerUnavailable;
        foreach (string server in sharing.Where(server => connector.StateFor(server, characterId) == ServerConnectionState.Connected))
        {
            (ExpectedEarnings? shared, OwnRunsReason why) = await _ReadServerAsync(dispatcher, registry, server, characterId, key, cancellationToken);
            if (shared is not null)
                return new SharedEarningsAttempt(shared, OwnRunsReason.None);

            // A server that answered "too few" tells the pilot more than one that could not be asked.
            if (reason is OwnRunsReason.ServerUnavailable || why is OwnRunsReason.TooFewSharedRuns)
                reason = why;
        }

        return new SharedEarningsAttempt(null, reason);
    }

    private async Task<(ExpectedEarnings? Shared, OwnRunsReason Reason)> _ReadServerAsync(IDispatcher dispatcher,
        IServerRegistry registry, string server, int characterId, ActivityStatsKey key, CancellationToken cancellationToken)
    {
        (EarningsFiguresDto Figures, int Pilots)? recent = await _FiguresAsync(dispatcher, server, characterId, key, RecentWindow,
            EarningsWindow.Recent, cancellationToken);
        if (recent is null)
            return (null, OwnRunsReason.ServerUnavailable);
        if (recent.Value.Pilots < RunOutcomesModule.MinimumPilots)
            return (null, OwnRunsReason.TooFewPilots);

        (EarningsFiguresDto Figures, int Pilots) taken = recent.Value;
        if (taken.Figures.Confidence is EarningsConfidence.None)
        {
            (EarningsFiguresDto Figures, int Pilots)? extended = await _FiguresAsync(dispatcher, server, characterId, key,
                ExtendedWindow, EarningsWindow.Extended, cancellationToken);
            if (extended is null)
                return (null, OwnRunsReason.ServerUnavailable);
            taken = extended.Value;
        }

        if (taken.Figures.Confidence is EarningsConfidence.None)
            return (null, taken.Pilots < RunOutcomesModule.MinimumPilots ? OwnRunsReason.TooFewPilots : OwnRunsReason.TooFewSharedRuns);

        return (new ExpectedEarnings(EarningsBasis.AllPilots, taken.Figures, taken.Pilots,
            await registry.DisplayNameAsync(server, cancellationToken)), OwnRunsReason.None);
    }

    /// <summary>The server's figures over one window with the pilots behind them, or null when the server could not
    /// answer. A server with too few pilots answers with no outcomes: its figures are empty and the pilot count is
    /// what tells the caller why.</summary>
    private async Task<(EarningsFiguresDto Figures, int Pilots)?> _FiguresAsync(IDispatcher dispatcher, string server,
        int characterId, ActivityStatsKey key, TimeSpan window, EarningsWindow label, CancellationToken cancellationToken)
    {
        DateOnly since = DateOnly.FromDateTime(DateTime.UtcNow - window);
        OutcomeReadResult<ActivityOutcomesDto> outcomes = await client.GetOutcomesAsync(server, characterId, key, since, cancellationToken);
        if (outcomes.Value is not { } read)
            return null;
        if (read.Availability is not SharedOutcomesAvailability.Shared)
            return (EarningsStatistics.Compute([], _ => null, EarningsPolicy.For(key.Kind), DateTime.UtcNow, label), read.PilotCount);

        OutcomeReadResult<ActivityOutcomeLootDto> loot = await client.GetOutcomeLootAsync(server, characterId, key, since, cancellationToken);
        if (loot.Value is not { } lines)
            return null;

        Result<EarningsFiguresDto> figures = await dispatcher.Query(new GetSharedEarningsQuery(read, lines, window, label), cancellationToken);
        return figures.Value is { } value ? (value, read.PilotCount) : null;
    }
}
