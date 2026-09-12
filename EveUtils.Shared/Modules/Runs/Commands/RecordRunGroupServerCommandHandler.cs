using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RecordRunGroupServerCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<RecordRunGroupServerCommand, Result>
{
    public async Task<Result> Handle(RecordRunGroupServerCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ServerAddress))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A server address is required.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<RunGroupOrigin>()
            .Where(origin => origin.GroupCode == command.GroupCode && origin.ServerAddress == null)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(origin => origin.ServerAddress, command.ServerAddress), cancellationToken);
        return Result.Success();
    }
}
