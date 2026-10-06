using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RepriceUnrecognisedLootCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde, IMarketPriceRepository marketPrices,
    IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<RepriceUnrecognisedLootCommand, Result<int>>
{
    // The SDE import, the hourly price refresh and the log's button can all ask at once. Two passes reading the same open
    // line would each add its entry, and the loot would count twice; one pass at a time, and the second finds nothing open.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<Result<int>> Handle(RepriceUnrecognisedLootCommand command, CancellationToken cancellationToken = default)
    {
        if (!sde.IsAvailable)
            return Result<int>.Success(0);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await _RepriceAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Result<int>> _RepriceAsync(CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<UnrecognisedLootLine> open = await db.Set<UnrecognisedLootLine>()
            .Include(line => line.RunLootCapture)
            .Where(line => line.Status == UnrecognisedItemStatus.Open)
            .ToListAsync(cancellationToken);

        List<(UnrecognisedLootLine Line, int TypeId)> recognised = [];
        foreach (UnrecognisedLootLine line in open)
            if (sde.TryGetTypeId(line.Name, out int typeId))
                recognised.Add((line, typeId));

        // A mission reward has no line while it is open: the reward row without a type id is the open record.
        List<Run> rewardRuns = await db.Set<Run>()
            .Include(run => run.Parameters)
            .Where(run => !run.DeletedAtUtc.HasValue
                          && run.Parameters.Any(parameter => parameter.ParameterKey == RunParameterKey.Item && parameter.ItemTypeId == null))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        List<(Run Run, RunParameter Parameter, int TypeId)> rewards = [];
        foreach (Run run in rewardRuns)
            foreach (RunParameter parameter in run.Parameters.Where(MissionRewardItems.IsUntyped))
                if (MissionRewardItems.NameOf(parameter) is { } name && sde.TryGetTypeId(name, out int rewardTypeId))
                    rewards.Add((run, parameter, rewardTypeId));

        if (recognised.Count == 0 && rewards.Count == 0)
            return Result<int>.Success(0);

        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(
            [.. recognised.Select(candidate => candidate.TypeId).Concat(rewards.Select(reward => reward.TypeId)).Distinct()],
            cancellationToken);
        DateTime nowUtc = DateTime.UtcNow;
        foreach ((Run run, RunParameter parameter, int typeId) in rewards)
        {
            parameter.ItemTypeId = typeId;
            db.Set<UnrecognisedLootLine>().Add(new UnrecognisedLootLine
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                CharacterId = run.CharacterId,
                Source = UnrecognisedItemSource.MissionReward,
                Name = MissionRewardItems.NameOf(parameter) ?? string.Empty,
                Quantity = (long)parameter.Amount.GetValueOrDefault(1),
                FirstSeenAtUtc = parameter.ObservedAtUtc,
                Status = UnrecognisedItemStatus.Resolved,
                ResolvedAtUtc = nowUtc,
                ResolvedTypeId = typeId,
                ResolvedUnitPrice = prices.TryGetValue(typeId, out double rewardPrice) ? (decimal)rewardPrice : null,
                ResolvedRunParameterId = parameter.Id
            });
        }

        foreach ((UnrecognisedLootLine line, int typeId) in recognised)
        {
            line.Status = UnrecognisedItemStatus.Resolved;
            line.ResolvedAtUtc = nowUtc;
            line.ResolvedTypeId = typeId;
            line.ResolvedUnitPrice = prices.TryGetValue(typeId, out double price) ? (decimal)price : null;

            // A capture that is left out of the totals gets its entry too: counting it again later must find the line
            // it was copied with, not a hole where the name was.
            if (line.RunLootCapture is not { } capture)
                continue;

            var entry = new RunLootEntry
            {
                Id = Guid.CreateVersion7(),
                RunLootCaptureId = capture.Id,
                ItemTypeId = typeId,
                Name = line.Name,
                Quantity = line.Quantity,
                LootKind = capture.Role is LootCaptureRole.Consumed ? LootKind.Lost : LootKind.Gained
            };
            db.Set<RunLootEntry>().Add(entry);
            line.ResolvedRunLootEntryId = entry.Id;
        }

        Guid[] runIds = [.. recognised
            .Where(candidate => candidate.Line.RunLootCapture is not null)
            .Select(candidate => candidate.Line.RunLootCapture!.RunId)
            .Concat(rewards.Select(reward => reward.Run.Id))
            .Distinct()];
        List<Run> runs = await db.Set<Run>()
            .Where(run => runIds.Contains(run.Id) && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        foreach (Run saved in runs.Where(run => run.State is RunState.Saved))
            RunLootWrites.MarkCorrected(saved);

        await db.SaveChangesAsync(cancellationToken);

        foreach (Run run in runs)
        {
            bool isSaved = run.State is RunState.Saved;
            if (isSaved)
                await dispatcher.Send(new RebuildActivitySummariesCommand(run.Id), cancellationToken);
            await eventBus.PublishAsync(new RunLootCapturedEvent(run.Id), EventTarget.Local, cancellationToken);
            if (isSaved)
                await eventBus.PublishAsync(new RunLootCorrectedEvent(run.Id), EventTarget.Local, cancellationToken);
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        }

        return Result<int>.Success(recognised.Count + rewards.Count);
    }
}
