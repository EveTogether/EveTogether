using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SaveRunsLeftUnfinishedCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : ICommandHandler<SaveRunsLeftUnfinishedCommand, Result<int>>
{
    /// <summary>Long enough that an evening's session, a break and a night's sleep all still leave the run for the
    /// pilot to finish; short enough that the pile ET-179 was written about cannot form again.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromHours(24);

    public async Task<Result<int>> Handle(SaveRunsLeftUnfinishedCommand command, CancellationToken cancellationToken = default)
    {
        DateTime cutoffUtc = command.NowUtc - Deadline;
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var overdue = await db.Set<Run>()
            .AsNoTracking()
            // On the stop stamp and never on the start: a run that ran for two days and was stopped a minute ago is
            // the pilot's to finish, and the deadline counts from the moment they walked away.
            .Where(run => run.State == RunState.Stopped && !run.DeletedAtUtc.HasValue
                          && run.StoppedAtUtc.HasValue && run.StoppedAtUtc.Value <= cutoffUtc)
            .Select(run => new { run.Id, StoppedAtUtc = run.StoppedAtUtc ?? command.NowUtc })
            .ToListAsync(cancellationToken);

        int saved = 0;
        foreach (var run in overdue)
        {
            // Through SAVE itself rather than a second write of the same transition: this is the very save the
            // pilot did not do, and one of the two drifting from the other is how a run ends up half-committed.
            Result result = await dispatcher.Send(
                new SaveRunCommand(run.Id, run.StoppedAtUtc, command.NowUtc, [], [], [], [],
                    AutoSavedAtUtc: command.NowUtc), cancellationToken);
            if (result.IsSuccess)
                saved++;
        }

        return Result<int>.Success(saved);
    }
}
