using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Commands;

[ClientOnly]
internal sealed class SetKillmailRunLinkCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : ICommandHandler<SetKillmailRunLinkCommand, Result>
{
    public async Task<Result> Handle(SetKillmailRunLinkCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        LocalKillmail? loss = await db.Set<LocalKillmail>().FirstOrDefaultAsync(killmail =>
            killmail.CharacterId == command.CharacterId && killmail.KillmailId == command.KillmailId, cancellationToken);
        if (loss is null)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The killmail no longer exists.", "Killmails"));
        }

        if (!loss.IsLoss)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only a loss can be linked to a run.", "Killmails"));
        }

        if (command.RunId is { } runId && !await db.Set<Run>().AnyAsync(run =>
                run.Id == runId && run.CharacterId == command.CharacterId && !run.DeletedAtUtc.HasValue, cancellationToken))
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "That run is not one of this character's runs.", "Killmails"));
        }

        Guid? previousRunId = loss.RunId;
        // The pod that followed this ship goes along, or it would keep its implants on a run that lost no ship.
        List<LocalKillmail> moved = [loss, .. await _FollowingPodsAsync(db, loss, cancellationToken)];
        foreach (LocalKillmail killmail in moved)
        {
            killmail.RunId = command.RunId;
            killmail.LinkSource = KillmailLinkSource.Manual;
        }

        await db.SaveChangesAsync(cancellationToken);
        // Both activities change: the one the loss left and the one it joined.
        foreach (Guid affected in new[] { previousRunId, command.RunId }.OfType<Guid>().Distinct())
        {
            await dispatcher.Send(new RebuildActivitySummariesCommand(affected), cancellationToken);
        }

        return Result.Success();
    }

    private static async Task<List<LocalKillmail>> _FollowingPodsAsync(ClientDbContext db, LocalKillmail ship,
        CancellationToken cancellationToken)
    {
        if (ship.RunId is null || KillmailRunLinker.IsCapsule(ship.VictimShipTypeId))
        {
            return [];
        }

        DateTime untilUtc = ship.KillmailTimeUtc + KillmailRunLinker.CapsuleGrace;
        return await db.Set<LocalKillmail>()
            .Where(pod => pod.CharacterId == ship.CharacterId && pod.IsLoss && pod.RunId == ship.RunId
                          && pod.LinkSource == KillmailLinkSource.Auto
                          && KillmailRunLinker.CapsuleTypeIds.Contains(pod.VictimShipTypeId)
                          && pod.KillmailTimeUtc >= ship.KillmailTimeUtc && pod.KillmailTimeUtc <= untilUtc)
            .ToListAsync(cancellationToken);
    }
}
