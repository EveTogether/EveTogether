using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
// Outside the .Entities namespace the enclosing namespace "Fleet" shadows the entity type of the same name.
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fleet.Repositories.Implementations;

internal sealed class FleetRepository(IDbContextFactory<SharedDbContext> contextFactory) : IFleetRepository
{
    public async Task<long> AddAsync(FleetEntity fleet, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetEntity>().Add(fleet);
        await db.SaveChangesAsync(cancellationToken);
        return fleet.Id;
    }

    public async Task<FleetEntity?> GetAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fleetId, cancellationToken);
    }

    public async Task UpdateAsync(FleetEntity fleet, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Load tracked and copy scalar values instead of Update(detached), which marks every column modified and so
        // overwrites fields a concurrent request changed in between (lost update). Same approach as UpdateMemberAsync.
        var tracked = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleet.Id, cancellationToken);
        if (tracked is null)
            return;
        db.Entry(tracked).CurrentValues.SetValues(fleet);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetEntity>> ListByCreatorAsync(int creatorCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Sort by Id (insertion order) — SQLite cannot ORDER BY a DateTimeOffset column.
        return await db.Set<FleetEntity>()
            .Where(f => f.CreatorCharacterId == creatorCharacterId)
            .OrderByDescending(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetEntity>> ListForParticipantAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var memberFleetIds = db.Set<FleetMember>().Where(m => m.CharacterId == characterId).Select(m => m.FleetId);
        // A concluded fleet is finished — it is hidden everywhere, so it never shows in MyFleets/Participating either (2026-06-10).
        return await db.Set<FleetEntity>()
            .Where(f => f.State == FleetState.Active
                        && f.Activation != FleetActivation.Concluded
                        && (f.CreatorCharacterId == characterId || memberFleetIds.Contains(f.Id)))
            .OrderByDescending(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetEntity>> ListOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // A concluded fleet is finished — it cannot be joined, so it is never offered in discovery (2026-06-04).
        return await db.Set<FleetEntity>()
            .Where(f => f.Visibility == FleetVisibility.Public
                        && f.State == FleetState.Active
                        && f.Activation != FleetActivation.Concluded)
            .OrderByDescending(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetEntity>> ListByStateAsync(FleetState state, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetEntity>()
            .Where(f => f.State == state)
            .OrderBy(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, int>> CountFleetsByCompositionIdsAsync(IReadOnlyCollection<long> compositionIds, CancellationToken cancellationToken = default)
    {
        if (compositionIds.Count == 0)
            return new Dictionary<long, int>();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var counts = await db.Set<FleetEntity>()
            .Where(f => f.FleetCompositionId != null && compositionIds.Contains(f.FleetCompositionId.Value))
            .GroupBy(f => f.FleetCompositionId!.Value)
            .Select(g => new { CompositionId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(x => x.CompositionId, x => x.Count);
    }

    public async Task TouchActivityAsync(long fleetId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, cancellationToken);
        if (fleet is null)
            return;
        fleet.LastActivityAt = at;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, cancellationToken);
        if (fleet is null)
            return;
        db.Set<FleetEntity>().Remove(fleet); // wings/squads/members/invites cascade via FK
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> AddWingAsync(FleetWing wing, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetWing>().Add(wing);
        await db.SaveChangesAsync(cancellationToken);
        return wing.Id;
    }

    public async Task<FleetWing?> GetWingAsync(long wingId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetWing>()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == wingId, cancellationToken);
    }

    public async Task UpdateWingAsync(FleetWing wing, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Tracked + SetValues, not Update(detached) — avoids the lost-update over all columns (see UpdateAsync).
        var tracked = await db.Set<FleetWing>().FirstOrDefaultAsync(w => w.Id == wing.Id, cancellationToken);
        if (tracked is null)
            return;
        db.Entry(tracked).CurrentValues.SetValues(wing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteWingAsync(long wingId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var wing = await db.Set<FleetWing>().FirstOrDefaultAsync(w => w.Id == wingId, cancellationToken);
        if (wing is null)
            return;
        db.Set<FleetWing>().Remove(wing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetWing>> ListWingsAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetWing>()
            .Where(w => w.FleetId == fleetId)
            .OrderBy(w => w.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<long> AddSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetSquad>().Add(squad);
        await db.SaveChangesAsync(cancellationToken);
        return squad.Id;
    }

    public async Task<FleetSquad?> GetSquadAsync(long squadId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetSquad>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == squadId, cancellationToken);
    }

    public async Task UpdateSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Tracked + SetValues, not Update(detached) — avoids the lost-update over all columns (see UpdateAsync).
        var tracked = await db.Set<FleetSquad>().FirstOrDefaultAsync(s => s.Id == squad.Id, cancellationToken);
        if (tracked is null)
            return;
        db.Entry(tracked).CurrentValues.SetValues(squad);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSquadAsync(long squadId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var squad = await db.Set<FleetSquad>().FirstOrDefaultAsync(s => s.Id == squadId, cancellationToken);
        if (squad is null)
            return;
        db.Set<FleetSquad>().Remove(squad);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetSquad>> ListSquadsAsync(long wingId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetSquad>()
            .Where(s => s.WingId == wingId)
            .OrderBy(s => s.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<long> AddInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetInvite>().Add(invite);
        await db.SaveChangesAsync(cancellationToken);
        return invite.Id;
    }

    public async Task<FleetInvite?> GetInviteAsync(long inviteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetInvite>()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken);
    }

    public async Task UpdateInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Tracked + SetValues, not Update(detached) — avoids the lost-update over all columns (see UpdateAsync).
        var tracked = await db.Set<FleetInvite>().FirstOrDefaultAsync(i => i.Id == invite.Id, cancellationToken);
        if (tracked is null)
            return;
        db.Entry(tracked).CurrentValues.SetValues(invite);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForInviteeAsync(int inviteeCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetInvite>()
            .Where(i => i.InviteeCharacterId == inviteeCharacterId && i.Status == FleetInviteStatus.Pending)
            .OrderBy(i => i.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForFleetAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetInvite>()
            .Where(i => i.FleetId == fleetId && i.Status == FleetInviteStatus.Pending)
            .OrderBy(i => i.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasPendingInviteAsync(long fleetId, int inviteeCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetInvite>()
            .AnyAsync(i => i.FleetId == fleetId && i.InviteeCharacterId == inviteeCharacterId && i.Status == FleetInviteStatus.Pending,
                cancellationToken);
    }

    public async Task<long> AddJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetJoinRequest>().Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return request.Id;
    }

    public async Task<FleetJoinRequest?> GetJoinRequestAsync(long requestId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetJoinRequest>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
    }

    public async Task UpdateJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Tracked + SetValues, not Update(detached) — avoids the lost-update over all columns (see UpdateAsync).
        var tracked = await db.Set<FleetJoinRequest>().FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
        if (tracked is null)
            return;
        db.Entry(tracked).CurrentValues.SetValues(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetJoinRequest>> ListPendingJoinRequestsForFleetAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetJoinRequest>()
            .Where(r => r.FleetId == fleetId && r.Status == FleetJoinRequestStatus.Pending)
            .OrderBy(r => r.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasPendingJoinRequestAsync(long fleetId, int requesterCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetJoinRequest>()
            .AnyAsync(r => r.FleetId == fleetId && r.RequesterCharacterId == requesterCharacterId && r.Status == FleetJoinRequestStatus.Pending,
                cancellationToken);
    }

    public async Task<long> AddMemberAsync(FleetMember member, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<FleetMember>().Add(member);
        await db.SaveChangesAsync(cancellationToken);
        return member.Id;
    }

    public async Task<bool> IsMemberAsync(long fleetId, int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetMember>()
            .AnyAsync(m => m.FleetId == fleetId && m.CharacterId == characterId, cancellationToken);
    }

    public async Task<IReadOnlyList<ActiveFleetMembership>> ListActiveMembershipsAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var memberFleetIds = db.Set<FleetMember>().Where(m => m.CharacterId == characterId).Select(m => m.FleetId);
        // No ORDER BY here: SQLite cannot sort a DateTimeOffset. Callers rank ActivatedAt in memory.
        return await db.Set<FleetEntity>()
            .Where(f => f.State == FleetState.Active
                        && f.Activation == FleetActivation.Active
                        && memberFleetIds.Contains(f.Id))
            .Select(f => new ActiveFleetMembership(f.Id, f.Name, f.ActivatedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FleetMember>> ListMembersAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetMember>()
            .Where(m => m.FleetId == fleetId)
            .OrderBy(m => m.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<FleetMember?> GetMemberAsync(long memberId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<FleetMember>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
    }

    public async Task UpdateMemberAsync(FleetMember member, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Load tracked (the owned AssignedFit snapshot loads with it) so clearing/replacing the owned fit applies —
        // a detached Update with a null owned navigation leaves the old owned columns in place.
        var tracked = await db.Set<FleetMember>().FirstOrDefaultAsync(m => m.Id == member.Id, cancellationToken);
        if (tracked is null)
            return;

        db.Entry(tracked).CurrentValues.SetValues(member);   // scalar fields (role, wing/squad, assigned-entry-id, …)
        tracked.AssignedFit = member.AssignedFit;             // owned type — tracked, so EF deletes/replaces it correctly
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMembersAsync(FleetMember first, FleetMember second, CancellationToken cancellationToken = default)
    {
        // Both members in ONE context + ONE SaveChanges (stream G member-swap): the position exchange is atomic, so the
        // roster never persists half-swapped.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var member in new[] { first, second })
        {
            var tracked = await db.Set<FleetMember>().FirstOrDefaultAsync(m => m.Id == member.Id, cancellationToken);
            if (tracked is null)
                continue;
            db.Entry(tracked).CurrentValues.SetValues(member);
            tracked.AssignedFit = member.AssignedFit;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(long memberId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.Set<FleetMember>().FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
            return;
        db.Set<FleetMember>().Remove(member);
        await db.SaveChangesAsync(cancellationToken);
    }
}
