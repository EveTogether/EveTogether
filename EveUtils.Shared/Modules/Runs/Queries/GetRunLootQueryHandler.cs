using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunLootQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunLootQuery, Result<RunLootOverview>>
{
    public async Task<Result<RunLootOverview>> Handle(GetRunLootQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        bool exists = await db.Set<Run>()
            .AnyAsync(run => run.Id == query.RunId && !run.DeletedAtUtc.HasValue, cancellationToken);
        if (!exists)
            return Result<RunLootOverview>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        List<RunLootCapture> captures = await db.Set<RunLootCapture>()
            .AsNoTracking()
            .Where(capture => capture.RunId == query.RunId)
            .Include(capture => capture.Entries)
            .OrderBy(capture => capture.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        return Result<RunLootOverview>.Success(new RunLootOverview(query.RunId, [.. captures.Select(RunLootCaptureMapper.ToDto)]));
    }
}
