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
    public async Task<IReadOnlyList<Fleet>> ListFleetsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Set<Fleet>().AsNoTracking().OrderByDescending(f => f.Id).ToListAsync(ct);
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
}
