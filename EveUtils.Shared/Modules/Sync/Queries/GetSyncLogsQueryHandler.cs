using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Sync.Dtos;
using EveUtils.Shared.Modules.Sync.Repositories;

namespace EveUtils.Shared.Modules.Sync.Queries;

internal sealed class GetSyncLogsQueryHandler(ISyncLogRepository repository)
    : IQueryHandler<GetSyncLogsQuery, IReadOnlyList<SyncLogDto>>
{
    public async Task<IReadOnlyList<SyncLogDto>> Handle(GetSyncLogsQuery query, CancellationToken cancellationToken = default)
    {
        var logs = await repository.ListAsync(cancellationToken);
        return logs.Select(l => new SyncLogDto(l.Id, l.EntityName, l.SyncedAtUtc, l.Note)).ToList();
    }
}
