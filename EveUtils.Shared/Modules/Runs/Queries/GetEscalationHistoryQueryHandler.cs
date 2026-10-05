using System.Globalization;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetEscalationHistoryQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetEscalationHistoryQuery, Result<IReadOnlyList<int>>>
{
    public async Task<Result<IReadOnlyList<int>>> Handle(
        GetEscalationHistoryQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<string> registered = await db.Set<RunParameter>().AsNoTracking()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.EscalationDungeonId
                && parameter.Run != null
                && parameter.Run.SiteTypeId == query.SourceDungeonId
                && parameter.Run.SiteTypeSource == SiteTypeSource.Site
                && !parameter.Run.DeletedAtUtc.HasValue)
            .Select(parameter => parameter.TypedValue)
            .ToListAsync(cancellationToken);

        List<int> dungeonIds = [.. registered
            .Select(stored => int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                ? id
                : (int?)null)
            .OfType<int>()
            .GroupBy(id => id)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)];
        return Result<IReadOnlyList<int>>.Success(dungeonIds);
    }
}
