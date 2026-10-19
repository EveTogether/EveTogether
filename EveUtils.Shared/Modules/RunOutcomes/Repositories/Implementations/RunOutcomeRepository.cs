using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.RunOutcomes.Repositories.Implementations;

internal sealed class RunOutcomeRepository(IDbContextFactory<SharedDbContext> contextFactory) : IRunOutcomeRepository
{
    public async Task<IReadOnlyList<RunOutcome>> ListAsync(IReadOnlyCollection<string> keyCodes, DateOnly since,
        CancellationToken cancellationToken = default)
    {
        DateTime sinceUtc = _Midnight(since);
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>()
            .AsNoTracking()
            .Where(outcome => keyCodes.Contains(outcome.KeyCode) && outcome.CompletedOn >= sinceUtc)
            .OrderBy(outcome => outcome.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunOutcome>> ListWithLootAsync(string keyCode, DateOnly since,
        CancellationToken cancellationToken = default)
    {
        DateTime sinceUtc = _Midnight(since);
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>()
            .AsNoTracking()
            .Where(outcome => outcome.KeyCode == keyCode && outcome.CompletedOn >= sinceUtc)
            .Include(outcome => outcome.LootLines)
            .OrderBy(outcome => outcome.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunOutcome>> ListSinceAsync(DateOnly since, CancellationToken cancellationToken = default)
    {
        DateTime sinceUtc = _Midnight(since);
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>()
            .AsNoTracking()
            .Where(outcome => outcome.CompletedOn >= sinceUtc)
            .OrderBy(outcome => outcome.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>().CountAsync(cancellationToken);
    }

    public async Task<RunOutcomeShareStatus> UpsertAsync(RunOutcome outcome, CancellationToken cancellationToken = default)
    {
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunOutcome? stored = await db.Set<RunOutcome>()
            .Include(existing => existing.LootLines)
            .FirstOrDefaultAsync(existing => existing.ActivityId == outcome.ActivityId, cancellationToken);
        if (stored is null)
            return await _InsertAsync(db, outcome, cancellationToken);

        if (stored.ContributorBucket != outcome.ContributorBucket)
            return RunOutcomeShareStatus.SharedByAnother;

        if (outcome.Revision <= stored.Revision)
            return RunOutcomeShareStatus.Stale;

        _CopyInto(stored, outcome);
        db.Set<RunOutcomeLootLine>().RemoveRange(stored.LootLines);
        stored.LootLines = [.. outcome.LootLines];
        await db.SaveChangesAsync(cancellationToken);
        outcome.Id = stored.Id;
        return RunOutcomeShareStatus.Updated;
    }

    public async Task<(RunOutcomeShareStatus Status, long? OutcomeId)> RemoveAsync(string activityId, string contributorBucket,
        long revision, CancellationToken cancellationToken = default)
    {
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunOutcome? stored = await db.Set<RunOutcome>()
            .FirstOrDefaultAsync(existing => existing.ActivityId == activityId, cancellationToken);
        if (stored is null)
            return (RunOutcomeShareStatus.NotStored, null);

        if (stored.ContributorBucket != contributorBucket)
            return (RunOutcomeShareStatus.SharedByAnother, null);

        if (revision < stored.Revision)
            return (RunOutcomeShareStatus.Stale, null);

        db.Remove(stored);
        await db.SaveChangesAsync(cancellationToken);
        return (RunOutcomeShareStatus.Removed, stored.Id);
    }

    public async Task<int> RemoveAllOfAsync(string contributorBucket, CancellationToken cancellationToken = default)
    {
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>()
            .Where(outcome => outcome.ContributorBucket == contributorBucket)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> RemoveCompletedBeforeAsync(DateOnly completedBefore, CancellationToken cancellationToken = default)
    {
        DateTime completedBeforeUtc = _Midnight(completedBefore);
        await using SharedDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunOutcome>()
            .Where(outcome => outcome.CompletedOn < completedBeforeUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Two fleet mates sharing the same activity at once both find no row; the unique ActivityId lets one insert win.
    private static async Task<RunOutcomeShareStatus> _InsertAsync(SharedDbContext db, RunOutcome outcome,
        CancellationToken cancellationToken)
    {
        db.Set<RunOutcome>().Add(outcome);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return RunOutcomeShareStatus.Stored;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            bool ownedByAnother = await db.Set<RunOutcome>()
                .AnyAsync(existing => existing.ActivityId == outcome.ActivityId
                                      && existing.ContributorBucket != outcome.ContributorBucket, cancellationToken);
            return ownedByAnother ? RunOutcomeShareStatus.SharedByAnother : RunOutcomeShareStatus.Stale;
        }
    }

    private static DateTime _Midnight(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static void _CopyInto(RunOutcome stored, RunOutcome shared)
    {
        stored.KeyCode = shared.KeyCode;
        stored.StatsKind = shared.StatsKind;
        stored.SiteTypeSource = shared.SiteTypeSource;
        stored.TypeId = shared.TypeId;
        stored.NormalizedName = shared.NormalizedName;
        stored.MissionLevel = shared.MissionLevel;
        stored.AbyssalTier = shared.AbyssalTier;
        stored.AbyssalWeather = shared.AbyssalWeather;
        stored.CompletedOn = shared.CompletedOn;
        stored.DurationSeconds = shared.DurationSeconds;
        stored.FixedIsk = shared.FixedIsk;
        stored.FleetSize = shared.FleetSize;
        stored.LootBasis = shared.LootBasis;
        stored.OpenUnrecognisedLootCount = shared.OpenUnrecognisedLootCount;
        stored.Revision = shared.Revision;
    }
}
