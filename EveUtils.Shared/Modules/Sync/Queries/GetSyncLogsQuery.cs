using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Sync.Dtos;

namespace EveUtils.Shared.Modules.Sync.Queries;

public sealed record GetSyncLogsQuery : IQuery<IReadOnlyList<SyncLogDto>>;
