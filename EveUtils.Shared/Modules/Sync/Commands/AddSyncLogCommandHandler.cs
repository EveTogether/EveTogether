using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Sync.Entities;
using EveUtils.Shared.Modules.Sync.Repositories;

namespace EveUtils.Shared.Modules.Sync.Commands;

internal sealed class AddSyncLogCommandHandler(ISyncLogRepository repository) : ICommandHandler<AddSyncLogCommand, Result<int>>
{
    public async Task<Result<int>> Handle(AddSyncLogCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = await repository.AddAsync(
                new SyncLog { EntityName = command.EntityName, SyncedAtUtc = command.SyncedAtUtc, Note = command.Note },
                cancellationToken);

            return Result<int>.Success(id);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ServerError, $"Failed to add sync log: {ex.Message}", "Sync"));
        }
    }
}
