using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunLootCaptureExclusionCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<SetRunLootCaptureExclusionCommand, Result>
{
    public async Task<Result> Handle(SetRunLootCaptureExclusionCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunLootCapture? capture = await db.Set<RunLootCapture>()
            .FirstOrDefaultAsync(c => c.Id == command.CaptureId, cancellationToken);
        if (capture is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "That loot capture no longer exists.", "Runs"));

        capture.IsExcluded = command.IsExcluded;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
