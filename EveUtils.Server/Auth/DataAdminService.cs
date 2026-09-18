using EveUtils.Server.DataExplorer;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Auth;

/// <summary>
/// Server data overview + delete for the panel. Reuses the existing delete seams where there is one
/// (SharedFit / ServerSession / FleetComposition), soft-deletes fleets via the lifecycle command (with a raw
/// "purge now" option), and falls back to raw removal for entities without a seam. Token-holding entities are
/// only ever shown as metadata (Iron Law #8) — the UI never surfaces token values.
/// </summary>
public sealed class DataAdminService(
    IDbContextFactory<ServerDbContext> contextFactory,
    ISharedFitRepository sharedFits,
    IServerAuthRepository serverAuth,
    IFleetCompositionRepository compositions,
    IDispatcher dispatcher) : IScopedService
{
    // ── Shared fittings (seam: ISharedFitRepository) ──────────────────────────────────────────────
    public Task<IReadOnlyList<SharedFit>> ListSharedFitsAsync(CancellationToken ct = default) =>
        sharedFits.ListAsync(ct);

    public Task DeleteSharedFitAsync(int id, CancellationToken ct = default) =>
        sharedFits.RemoveAsync(id, ct);

    // ── Fleets (soft-disband via command default; raw purge optional) ─────────────────────────────
    /// <summary>Every fleet with its member ids and its composition's name: three flat reads joined in memory, because
    /// the fleet list shows all of them at once and one query per fleet would grow with the list.</summary>
    public async Task<IReadOnlyList<FleetListItem>> ListFleetItemsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleets = await db.Set<Fleet>().AsNoTracking().ToListAsync(ct);
        var members = await db.Set<FleetMember>().AsNoTracking()
            .Select(m => new { m.FleetId, m.CharacterId, m.Role })
            .ToListAsync(ct);
        var compositionNames = await db.Set<FleetComposition>().AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var membersByFleet = members
            .GroupBy(m => m.FleetId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Role).Select(m => m.CharacterId).ToList());
        return fleets
            .Select(f => new FleetListItem
            {
                Fleet = f,
                MemberCharacterIds = membersByFleet.GetValueOrDefault(f.Id) ?? [],
                CompositionName = f.FleetCompositionId is { } compositionId ? compositionNames.GetValueOrDefault(compositionId) : null,
            })
            .ToList();
    }

    /// <summary>The opened fleet with its wings, squads and members, and — when its composition still exists — how far
    /// the assigned members fill each role. Null when the fleet is gone.</summary>
    public async Task<FleetDetail?> GetFleetDetailAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fleet is null)
            return null;

        var wings = await db.Set<FleetWing>().AsNoTracking().Where(w => w.FleetId == id).OrderBy(w => w.Id).ToListAsync(ct);
        var wingIds = wings.Select(w => w.Id).ToList();
        var squads = await db.Set<FleetSquad>().AsNoTracking().Where(s => wingIds.Contains(s.WingId)).OrderBy(s => s.Id).ToListAsync(ct);
        var members = await db.Set<FleetMember>().AsNoTracking().Where(m => m.FleetId == id).ToListAsync(ct);

        var fitIds = members.Select(m => m.AssignedFit?.ServerSharedFitId).OfType<int>().Distinct().ToList();
        var composition = fleet.FleetCompositionId is { } compositionId
            ? await db.Set<FleetComposition>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == compositionId, ct)
            : null;
        IReadOnlyList<RoleCoverage> coverage = [];
        if (composition is not null)
        {
            var roles = await db.Set<FleetCompositionRole>().AsNoTracking()
                .Where(r => r.CompositionId == composition.Id).OrderBy(r => r.SortOrder).ToListAsync(ct);
            var roleIds = roles.Select(r => r.Id).ToList();
            var entries = await db.Set<FleetCompositionEntry>().AsNoTracking()
                .Where(e => roleIds.Contains(e.RoleId)).OrderBy(e => e.SortOrder).ToListAsync(ct);
            coverage = _Coverage(roles, entries, members);
            fitIds.AddRange(entries.Select(e => e.Fit.ServerSharedFitId).OfType<int>());
        }

        var existingFitIds = await db.Set<SharedFit>().AsNoTracking()
            .Where(f => fitIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        return new FleetDetail
        {
            Fleet = fleet,
            Wings = wings,
            Squads = squads,
            Members = members,
            Composition = composition,
            Coverage = coverage,
            ExistingSharedFitIds = existingFitIds.ToHashSet(),
        };
    }

    private static List<RoleCoverage> _Coverage(
        IReadOnlyList<FleetCompositionRole> roles, IReadOnlyList<FleetCompositionEntry> entries, IReadOnlyList<FleetMember> members)
    {
        var assignedPerEntry = members
            .Where(m => m.AssignedCompositionEntryId is not null)
            .GroupBy(m => m.AssignedCompositionEntryId)
            .ToDictionary(g => g.Key ?? 0, g => g.Count());
        return roles
            .Select(role =>
            {
                var roleEntries = entries.Where(e => e.RoleId == role.Id).ToList();
                var entryMinimums = roleEntries.Select(e => e.EntryMinCount).OfType<int>().ToList();
                return new RoleCoverage
                {
                    RoleName = role.RoleName,
                    Assigned = roleEntries.Sum(e => assignedPerEntry.GetValueOrDefault(e.Id)),
                    Target = role.GroupMinCount ?? (entryMinimums.Count > 0 ? entryMinimums.Sum() : null),
                    Entries = roleEntries
                        .Select(e => new EntryCoverage
                        {
                            FitName = e.Fit.FitName,
                            ShipTypeId = e.Fit.ShipTypeId,
                            ServerSharedFitId = e.Fit.ServerSharedFitId,
                            Assigned = assignedPerEntry.GetValueOrDefault(e.Id),
                            Minimum = e.EntryMinCount,
                        })
                        .ToList(),
                };
            })
            .ToList();
    }

    /// <summary>Soft-delete (→ Archived) via the lifecycle command, dispatched as the fleet's own creator so the
    /// creator-gate passes and the full disband lifecycle runs (members freed, etc.). The cleanup sweep hard-
    /// deletes archived fleets after 24h.</summary>
    public async Task<bool> DisbandFleetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fleet is null)
            return false;
        var result = await dispatcher.Send(new DisbandFleetCommand(id, fleet.CreatorCharacterId), ct);
        return result.IsSuccess;
    }

    /// <summary>Hard purge — raw removal; child FKs (wings/squads/members/invites) cascade.</summary>
    public async Task PurgeFleetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fleet is null)
            return;
        db.Remove(fleet);
        await db.SaveChangesAsync(ct);
    }

    // ── Fleet compositions (shared doctrines; seam: IFleetCompositionRepository) ───────────────────
    /// <summary>Every composition shared to this server. The desktop client pushes
    /// these but had no server-side view/delete until now.</summary>
    public Task<IReadOnlyList<FleetComposition>> ListFleetCompositionsAsync(CancellationToken ct = default) =>
        compositions.ListAllAsync(ct);

    /// <summary>Hard-deletes a shared composition via the repository seam; its roles and fit-entries cascade (FK).
    /// The panel's own DataDelete RBAC is the gate here (like the shared-fit delete), so this bypasses the
    /// per-character owner-or-manage check the client mutations use.</summary>
    public Task DeleteFleetCompositionAsync(long id, CancellationToken ct = default) =>
        compositions.DeleteAsync(id, ct);

    // ── Paired characters (metadata only; raw delete — no seam, orphan-aware) ──────────────────────
    public Task<IReadOnlyList<SyncedCharacter>> ListSyncedCharactersAsync(CancellationToken ct = default) =>
        serverAuth.ListSyncedAsync(ct);

    /// <summary>Removes a paired character + its sessions (cascade). Leaves loose scalar references
    /// (FleetMember.CharacterId / QueuedMessage.RecipientCharacterId / SharedFit.SharedByCharacterId) as
    /// orphans by design — the UI warns first. Breaks any connected client's session for that character.</summary>
    public async Task DeleteSyncedCharacterAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var synced = await db.Set<SyncedCharacter>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (synced is null)
            return;
        var sessions = await db.Set<ServerSession>().Where(s => s.SyncedCharacterId == id).ToListAsync(ct);
        db.RemoveRange(sessions);
        db.Remove(synced);
        await db.SaveChangesAsync(ct);
    }

    // ── Sessions (metadata only; seam: IServerAuthRepository) ──────────────────────────────────────
    public Task<IReadOnlyList<ServerSession>> ListSessionsAsync(CancellationToken ct = default) =>
        serverAuth.ListSessionsAsync(ct);

    public Task DeleteSessionAsync(int id, CancellationToken ct = default) =>
        serverAuth.DeleteSessionAsync(id, ct);

    // ── Cross-entity reads ─────────────────────────────────────────────────────────────────────────
    public async Task<DataCounts> CountAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var compositionIds = db.Set<FleetComposition>().Select(c => (long?)c.Id);
        return new DataCounts
        {
            Characters = await db.Set<SyncedCharacter>().CountAsync(ct),
            Fleets = await db.Set<Fleet>().CountAsync(ct),
            FleetsNeedingAttention = await db.Set<Fleet>()
                .CountAsync(f => f.FleetCompositionId != null && !compositionIds.Contains(f.FleetCompositionId), ct),
            Compositions = await db.Set<FleetComposition>().CountAsync(ct),
            SharedFits = await db.Set<SharedFit>().CountAsync(ct),
            Sessions = await db.Set<ServerSession>().CountAsync(ct),
        };
    }

    /// <summary>Fleets rostered in and fits shared, per ESI character id. Characters with neither are absent.</summary>
    public async Task<IReadOnlyDictionary<int, CharacterUsage>> CountUsageByCharacterAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleets = await db.Set<FleetMember>()
            .GroupBy(m => m.CharacterId)
            .Select(g => new { CharacterId = g.Key, Count = g.Select(m => m.FleetId).Distinct().Count() })
            .ToDictionaryAsync(x => x.CharacterId, x => x.Count, ct);
        var fits = await db.Set<SharedFit>()
            .GroupBy(f => f.SharedByCharacterId)
            .Select(g => new { CharacterId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CharacterId, x => x.Count, ct);
        return fleets.Keys.Union(fits.Keys).ToDictionary(
            id => id,
            id => new CharacterUsage { Fleets = fleets.GetValueOrDefault(id), SharedFits = fits.GetValueOrDefault(id) });
    }
}
