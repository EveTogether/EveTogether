using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SnapshotRunLossPricesCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, IEventBus eventBus)
    : ICommandHandler<SnapshotRunLossPricesCommand, Result<int>>
{
    public async Task<Result<int>> Handle(SnapshotRunLossPricesCommand command, CancellationToken cancellationToken = default)
    {
        if (command.RunIds.Count == 0)
            return Result<int>.Success(0);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Guid[] runIds = [.. command.RunIds.Distinct()];
        IReadOnlySet<Guid> changed = await RunLossPriceSnapshots.FixAsync(db, marketPrices, runIds, PriceSnapshotSource.Capture,
            cancellationToken);
        if (changed.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);
        Guid[] changedIds = [.. changed];
        var runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => changedIds.Contains(run.Id))
            .Select(run => new { run.Id, run.GroupCode })
            .ToListAsync(cancellationToken);
        foreach (var run in runs)
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);

        return Result<int>.Success(changed.Count);
    }
}
