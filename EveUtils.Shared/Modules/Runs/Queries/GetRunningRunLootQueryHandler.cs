using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

internal sealed class GetRunningRunLootQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunningRunLootQuery, Result<RunLootOverview>>
{
    public async Task<Result<RunLootOverview>> Handle(GetRunningRunLootQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken);
        if (run is null)
            return Result<RunLootOverview>.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                    "No run is running, so there is no loot to show.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{runningCount} runs are running, so which one's loot to show is ambiguous.", "Runs"));

        List<RunLootCapture> captures = await db.Set<RunLootCapture>()
            .AsNoTracking()
            .Where(capture => capture.RunId == run.Id)
            .Include(capture => capture.Entries)
            .OrderBy(capture => capture.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        return Result<RunLootOverview>.Success(new RunLootOverview(run.Id, [.. captures.Select(_ToDto)]));
    }

    private static RunLootCaptureDto _ToDto(RunLootCapture capture) => new(
        capture.Id, capture.CapturedAtUtc, capture.IsExcluded, capture.ContentHash,
        [.. capture.Entries.Select(entry => new RunLootEntryDto(entry.ItemTypeId, entry.Name, entry.Quantity, entry.ClipboardPrice, entry.LootKind))]);
}
