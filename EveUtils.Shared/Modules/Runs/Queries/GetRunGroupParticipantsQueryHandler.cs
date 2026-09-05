using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunGroupParticipantsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunGroupParticipantsQuery, Result<IReadOnlyList<RunGroupParticipantDto>>>
{
    public async Task<Result<IReadOnlyList<RunGroupParticipantDto>>> Handle(
        GetRunGroupParticipantsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<RunGroupParticipantDto> participants = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => !run.DeletedAtUtc.HasValue
                          && (query.GroupCode != null ? run.GroupCode == query.GroupCode : run.Id == query.RunId))
            .OrderBy(run => run.CharacterId)
            .Select(run => new RunGroupParticipantDto(run.Id, run.CharacterId, run.IsParticipant, run.IsPayoutEligible))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<RunGroupParticipantDto>>.Success(participants);
    }
}
