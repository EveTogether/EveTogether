using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class StartRunCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<StartRunCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(StartRunCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CharacterId <= 0)
            return Result<Guid>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A run requires a character.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Guid id = Guid.CreateVersion7();
        db.Set<Run>().Add(new Run
        {
            Id = id,
            CharacterId = command.CharacterId,
            GroupCode = command.GroupCode,
            ActivityKind = command.ActivityKind,
            State = RunState.Running,
            StartedAtUtc = command.StartedAtUtc,
            SiteTypeId = command.SiteTypeId,
            SiteName = command.SiteName,
            SolarSystemId = command.SolarSystemId,
            Signature = command.Signature,
            Role = command.Role,
            IsPayoutEligible = command.IsPayoutEligible,
            FitContentHash = command.FitContentHash,
            FitNameSnapshot = command.FitNameSnapshot,
            SyncState = RunSyncState.Local,
            Revision = 1
        });
        await db.SaveChangesAsync(cancellationToken);
        return Result<Guid>.Success(id);
    }
}
