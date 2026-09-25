using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Plans.Repositories.Implementations;

/// <summary>SQLite-backed skill-plan store. Client-only, since only the <c>ClientDbContext</c> maps the entities.</summary>
internal sealed class SkillPlanRepository(IDbContextFactory<SharedDbContext> contextFactory)
    : ISkillPlanRepository, ISingletonService
{
    public async Task<IReadOnlyList<SkillPlan>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<SkillPlan>()
            .AsNoTracking()
            .Where(plan => plan.CharacterId == characterId)
            .OrderByDescending(plan => plan.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<SkillPlan?> GetAsync(int planId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<SkillPlan>().AsNoTracking().FirstOrDefaultAsync(plan => plan.Id == planId, cancellationToken);
    }

    public async Task<IReadOnlyList<SkillPlanRow>> GetRowsAsync(int planId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<SkillPlanRow>()
            .AsNoTracking()
            .Where(row => row.PlanId == planId)
            .OrderBy(row => row.Position)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CreateAsync(SkillPlan plan, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<SkillPlan>().Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return plan.Id;
    }

    public async Task<bool> RenameAsync(int planId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.Set<SkillPlan>().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return false;
        }

        plan.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(int planId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.Set<SkillPlan>().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return false;
        }

        var rows = await db.Set<SkillPlanRow>().Where(row => row.PlanId == planId).ToListAsync(cancellationToken);
        db.Set<SkillPlanRow>().RemoveRange(rows);
        db.Set<SkillPlan>().Remove(plan);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> AddRowsAsync(int planId, IReadOnlyList<SkillPlanRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<SkillPlanRow>().Where(row => row.PlanId == planId).ToListAsync(cancellationToken);
        var have = new HashSet<(int SkillTypeId, int Level)>(existing.Select(row => (row.SkillTypeId, row.Level)));
        int nextPosition = existing.Count == 0 ? 0 : existing.Max(row => row.Position) + 1;

        var toAdd = new List<SkillPlanRow>();
        foreach (var row in rows)
        {
            if (!have.Add((row.SkillTypeId, row.Level)))
            {
                continue; // already in the plan, or repeated within this batch — dedupe on (skill, level)
            }

            row.PlanId = planId;
            row.Position = nextPosition++;
            toAdd.Add(row);
        }

        if (toAdd.Count == 0)
        {
            return 0;
        }

        db.Set<SkillPlanRow>().AddRange(toAdd);
        await db.SaveChangesAsync(cancellationToken);
        return toAdd.Count;
    }

    public async Task<bool> RemoveRowAsync(int planId, int skillTypeId, int level, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<SkillPlanRow>().FirstOrDefaultAsync(
            r => r.PlanId == planId && r.SkillTypeId == skillTypeId && r.Level == level, cancellationToken);
        if (row is null)
        {
            return false;
        }

        db.Set<SkillPlanRow>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
