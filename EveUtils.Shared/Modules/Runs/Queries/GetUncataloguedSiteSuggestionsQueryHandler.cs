using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetUncataloguedSiteSuggestionsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetUncataloguedSiteSuggestionsQuery, Result<IReadOnlyList<UncataloguedSiteSuggestionDto>>>
{
    public async Task<Result<IReadOnlyList<UncataloguedSiteSuggestionDto>>> Handle(
        GetUncataloguedSiteSuggestionsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recorded = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.ActivityKind == ActivityKind.Site
                && run.SiteTypeSource == SiteTypeSource.Uncatalogued
                && run.SiteName != null && run.SiteName != ""
                && run.SignatureGroupSnapshot != null && run.SignatureGroupSnapshot != "")
            .GroupBy(run => new { run.SiteName, run.SignatureGroupSnapshot })
            .Select(group => new
            {
                SiteName = group.Key.SiteName ?? string.Empty,
                SignatureGroup = group.Key.SignatureGroupSnapshot ?? string.Empty,
                LastStartedAtUtc = group.Max(run => run.StartedAtUtc)
            })
            .ToListAsync(cancellationToken);

        IReadOnlyList<UncataloguedSiteSuggestionDto> suggestions = [.. recorded
            .GroupBy(row => row.SiteName)
            .Select(byName => byName.OrderByDescending(row => row.LastStartedAtUtc).First())
            .OrderBy(row => row.SiteName, StringComparer.OrdinalIgnoreCase)
            .Select(row => new UncataloguedSiteSuggestionDto(row.SiteName, row.SignatureGroup))];
        return Result<IReadOnlyList<UncataloguedSiteSuggestionDto>>.Success(suggestions);
    }
}
