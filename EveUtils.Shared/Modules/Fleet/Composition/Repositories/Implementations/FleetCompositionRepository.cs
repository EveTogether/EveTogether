using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fleet.Composition.Repositories.Implementations;

internal sealed class FleetCompositionRepository(IDbContextFactory<SharedDbContext> contextFactory) : IFleetCompositionRepository
{
    public async Task<long> AddAsync(FleetComposition composition, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetComposition>().Add(composition);
        await db.SaveChangesAsync(cancellationToken);
        return composition.Id;
    }

    public async Task<FleetComposition?> GetAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetComposition>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);
    }

    public async Task UpdateAsync(FleetComposition composition, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetComposition>().Update(composition);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var composition = await db.Set<FleetComposition>().FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);
        if (composition is null)
            return;

        db.Set<FleetComposition>().Remove(composition); // roles + entries cascade (FK).
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetComposition>> ListByOwnerAsync(int ownerCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Sort by Id (insertion order) — SQLite cannot ORDER BY a DateTimeOffset column.
        return await db.Set<FleetComposition>()
            .Where(c => c.OwnerCharacterId == ownerCharacterId)
            .OrderByDescending(c => c.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetComposition>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Sort by Id (insertion order) — SQLite cannot ORDER BY a DateTimeOffset column.
        return await db.Set<FleetComposition>()
            .OrderByDescending(c => c.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<FleetCompositionGraph?> GetGraphAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var composition = await db.Set<FleetComposition>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);
        if (composition is null)
            return null;

        var roles = await db.Set<FleetCompositionRole>()
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var roleIds = roles.Select(r => r.Id).ToList();
        var entries = await db.Set<FleetCompositionEntry>()
            .Where(e => roleIds.Contains(e.RoleId))
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var entriesByRole = entries
            .GroupBy(e => e.RoleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FleetCompositionEntry>)g.ToList());

        var roleGraphs = roles
            .Select(r => new FleetCompositionRoleGraph(
                r, entriesByRole.TryGetValue(r.Id, out var roleEntries) ? roleEntries : []))
            .ToList();

        return new FleetCompositionGraph(composition, roleGraphs);
    }

    public async Task<long> AddRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetCompositionRole>().Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    public async Task<FleetCompositionRole?> GetRoleAsync(long roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetCompositionRole>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
    }

    public async Task UpdateRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetCompositionRole>().Update(role);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(long roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var role = await db.Set<FleetCompositionRole>().FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
            return;

        db.Set<FleetCompositionRole>().Remove(role); // entries cascade (FK).
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetCompositionRole>> ListRolesAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetCompositionRole>()
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var roles = await db.Set<FleetCompositionRole>()
            .Where(r => r.CompositionId == compositionId)
            .ToListAsync(cancellationToken);

        var positions = _ToPositionMap(orderedRoleIds);
        foreach (var role in roles)
            if (positions.TryGetValue(role.Id, out var position))
                role.SortOrder = position;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> AddEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetCompositionEntry>().Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }

    public async Task<FleetCompositionEntry?> GetEntryAsync(long entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The owned Fit snapshot loads with the entry (owned types are always included).
        return await db.Set<FleetCompositionEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
    }

    public async Task UpdateEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetCompositionEntry>().Update(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteEntryAsync(long entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Set<FleetCompositionEntry>().FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
            return;

        db.Set<FleetCompositionEntry>().Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetCompositionEntry>> ListEntriesAsync(long roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetCompositionEntry>()
            .Where(e => e.RoleId == roleId)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.Set<FleetCompositionEntry>()
            .Where(e => e.RoleId == roleId)
            .ToListAsync(cancellationToken);

        var positions = _ToPositionMap(orderedEntryIds);
        foreach (var entry in entries)
            if (positions.TryGetValue(entry.Id, out var position))
                entry.SortOrder = position;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Maps each id to its index in the desired order, keeping the first index when an id repeats.</summary>
    private static Dictionary<long, int> _ToPositionMap(IReadOnlyList<long> orderedIds)
    {
        var map = new Dictionary<long, int>(orderedIds.Count);
        for (var i = 0; i < orderedIds.Count; i++)
            map.TryAdd(orderedIds[i], i);
        return map;
    }
}
