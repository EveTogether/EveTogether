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
    private static readonly TimeSpan RetryWindow = TimeSpan.FromDays(1);

    public async Task<Result<int>> Handle(LinkKillmailsToRunsCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<LocalKillmail> losses = db.Set<LocalKillmail>()
            .Where(killmail => killmail.CharacterId == command.CharacterId && killmail.IsLoss);
        // Open is not the pilot's call and without a live run: runs are only soft-deleted, so an auto link to a folded
        // duplicate is open again. ponytail: only a loss of or imported in the last day is retried, so one outside any
        // run is not reloaded forever; widen RetryWindow if old runs get their times corrected.
        DateTime retryFromUtc = DateTime.UtcNow - RetryWindow;
        List<LocalKillmail> open = await losses
            .Where(killmail => killmail.LinkSource != KillmailLinkSource.Manual
                               && (killmail.KillmailTimeUtc >= retryFromUtc || killmail.ImportedAtUtc >= retryFromUtc)
                               && (killmail.RunId == null
                                   || db.Set<Run>().Any(run => run.Id == killmail.RunId && run.DeletedAtUtc != null)))
            // A pod lost in the same second as its ship comes after it, so it finds the ship already linked.
            .OrderBy(killmail => killmail.KillmailTimeUtc)
            .ThenBy(killmail => KillmailRunLinker.CapsuleTypeIds.Contains(killmail.VictimShipTypeId))
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return Result<int>.Success(0);
        }

        // The same tracked instances as open, so a capsule sees the ship linked a moment earlier in this pass.
        DateTime shipsFromUtc = open[0].KillmailTimeUtc - KillmailRunLinker.CapsuleGrace;
        List<LocalKillmail> nearby = await losses
            .Where(killmail => killmail.KillmailTimeUtc >= shipsFromUtc)
            .ToListAsync(cancellationToken);
        IReadOnlyList<LinkableRun> runs = await _RunsAsync(db, command.CharacterId, open[0].KillmailTimeUtc,
            open[^1].KillmailTimeUtc, cancellationToken);

        DateTime nowUtc = DateTime.UtcNow;
        List<Guid> linkedRunIds = [];
        foreach (LocalKillmail loss in open)
        {
            KillmailRunMatch match = KillmailRunLinker.Match(loss, runs,
                [.. nearby.Where(killmail => killmail.RunId is not null)], nowUtc);
            loss.RunId = match.RunId;
            loss.LinkSource = match.RunId is null ? KillmailLinkSource.None : KillmailLinkSource.Auto;
            if (match.RunId is { } runId)
            {
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
