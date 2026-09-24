using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Commands;

[ClientOnly]
internal sealed class LinkKillmailsToRunsCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IFittingRepository fittings, IDispatcher dispatcher)
    : ICommandHandler<LinkKillmailsToRunsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(LinkKillmailsToRunsCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<LocalKillmail> losses = db.Set<LocalKillmail>()
            .Where(killmail => killmail.CharacterId == command.CharacterId && killmail.IsLoss);
        // Unlinked and not the pilot's call: a loss whose run was deleted comes back here as Auto without a run.
        IQueryable<LocalKillmail> unlinked = losses
            .Where(killmail => killmail.RunId == null && killmail.LinkSource != KillmailLinkSource.Manual);
        if (!await unlinked.AnyAsync(cancellationToken))
        {
            return Result<int>.Success(0);
        }

        DateTime firstUtc = await unlinked.MinAsync(killmail => killmail.KillmailTimeUtc, cancellationToken);
        DateTime lastUtc = await unlinked.MaxAsync(killmail => killmail.KillmailTimeUtc, cancellationToken);
        // Tracked and in time order, so a capsule sees the ship linked a moment earlier in this same pass.
        DateTime shipsFromUtc = firstUtc - KillmailRunLinker.CapsuleGrace;
        List<LocalKillmail> nearby = await losses
            .Where(killmail => killmail.KillmailTimeUtc >= shipsFromUtc)
            .OrderBy(killmail => killmail.KillmailTimeUtc)
            .ToListAsync(cancellationToken);
        IReadOnlyList<LinkableRun> runs = await _RunsAsync(db, command.CharacterId, firstUtc, lastUtc, cancellationToken);

        DateTime nowUtc = DateTime.UtcNow;
        List<Guid> linkedRunIds = [];
        foreach (LocalKillmail loss in nearby.Where(killmail => killmail.RunId is null
                     && killmail.LinkSource != KillmailLinkSource.Manual).ToList())
        {
            KillmailRunMatch match = KillmailRunLinker.Match(loss, runs,
                [.. nearby.Where(killmail => killmail.RunId is not null)], nowUtc);
            if (match.RunId is { } runId)
            {
                loss.RunId = runId;
                loss.LinkSource = KillmailLinkSource.Auto;
                linkedRunIds.Add(runId);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (Guid runId in linkedRunIds.Distinct())
        {
            await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken);
        }

        return Result<int>.Success(linkedRunIds.Count);
    }

    private async Task<IReadOnlyList<LinkableRun>> _RunsAsync(ClientDbContext db, long characterId, DateTime firstUtc,
        DateTime lastUtc, CancellationToken cancellationToken)
    {
        DateTime stoppedFromUtc = firstUtc - KillmailRunLinker.StopGrace;
        var runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.CharacterId == characterId && !run.DeletedAtUtc.HasValue && run.StartedAtUtc <= lastUtc
                          && (run.StoppedAtUtc == null || run.StoppedAtUtc >= stoppedFromUtc))
            .Select(run => new
            {
                run.Id, run.CharacterId, run.ActivityKind, run.SolarSystemId, run.StartedAtUtc, run.StoppedAtUtc,
                run.FitContentHash
            })
            .ToListAsync(cancellationToken);

        Dictionary<string, int?> hullByFit = [];
        foreach (string hash in runs.Select(run => run.FitContentHash).OfType<string>().Distinct())
        {
            hullByFit[hash] = (await fittings.FindByContentHashAsync(hash, cancellationToken))?.ShipTypeId;
        }

        return [.. runs.Select(run => new LinkableRun(run.Id, run.CharacterId, run.ActivityKind, run.SolarSystemId,
            run.StartedAtUtc, run.StoppedAtUtc,
            run.FitContentHash is { } hash ? hullByFit[hash] : null))];
    }
}
