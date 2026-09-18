using System.Security.Claims;
using EveUtils.Server.DataExplorer;
using EveUtils.Server.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Auth;

/// <summary>
/// Server data overview + delete for the panel. Reuses the existing delete seams where there is one
/// (SharedFit / ServerSession / FleetComposition), soft-deletes fleets via the lifecycle command (with a raw
/// "purge now" option), and falls back to raw removal for entities without a seam. Token-holding entities are
/// only ever shown as metadata (Iron Law #8) — the UI never surfaces token values.
/// Every delete takes the acting admin and refuses without <see cref="PanelPermissions.DataDelete"/> itself: a
/// disabled or hidden button only decides what the page offers, not what a crafted circuit event can reach.
/// </summary>
public sealed class DataAdminService(
    IDbContextFactory<ServerDbContext> contextFactory,
    ISharedFitRepository sharedFits,
    IServerAuthRepository serverAuth,
    IFleetCompositionRepository compositions,
    IDispatcher dispatcher) : IScopedService
{
    // ── Shared fittings (seam: ISharedFitRepository) ──────────────────────────────────────────────
    public async Task<Result> DeleteSharedFitAsync(ClaimsPrincipal actor, int id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        return await sharedFits.RemoveAsync(id, ct) ? Result.Success() : _NotFound("shared fit");
    }

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
    public async Task<Result> DisbandFleetAsync(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fleet is null)
            return _NotFound("fleet");
        return await dispatcher.Send(new DisbandFleetCommand(id, fleet.CreatorCharacterId), ct);
    }

    /// <summary>Hard purge — raw removal; child FKs (wings/squads/members/invites) cascade.</summary>
    public async Task<Result> PurgeFleetAsync(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fleet is null)
            return _NotFound("fleet");
        db.Remove(fleet);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ── Fleet compositions (shared doctrines; seam: IFleetCompositionRepository) ───────────────────
    /// <summary>Hard-deletes a shared composition via the repository seam; its roles and fit-entries cascade (FK).
    /// The panel's own DataDelete RBAC is the gate here (like the shared-fit delete), so this bypasses the
    /// per-character owner-or-manage check the client mutations use.</summary>
    public async Task<Result> DeleteFleetCompositionAsync(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        await compositions.DeleteAsync(id, ct);
        return Result.Success();
    }

    // ── Paired characters (metadata only; raw delete — no seam, orphan-aware) ──────────────────────
    public Task<IReadOnlyList<SyncedCharacter>> ListSyncedCharactersAsync(CancellationToken ct = default) =>
        serverAuth.ListSyncedAsync(ct);

    /// <summary>Removes a paired character + its sessions (cascade). Leaves loose scalar references
    /// (FleetMember.CharacterId / QueuedMessage.RecipientCharacterId / SharedFit.SharedByCharacterId) as
    /// orphans by design — the UI warns first. Breaks any connected client's session for that character.</summary>
    public async Task<Result> DeleteSyncedCharacterAsync(ClaimsPrincipal actor, int id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var synced = await db.Set<SyncedCharacter>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (synced is null)
            return _NotFound("paired character");
        var sessions = await db.Set<ServerSession>().Where(s => s.SyncedCharacterId == id).ToListAsync(ct);
        db.RemoveRange(sessions);
        db.Remove(synced);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ── Sessions (metadata only; seam: IServerAuthRepository) ──────────────────────────────────────
    public async Task<IReadOnlyList<SessionListItem>> ListSessionItemsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return (await serverAuth.ListSessionsAsync(ct))
            .Select(s => new SessionListItem { Session = s, Status = SessionListItem.StatusOf(s.LastHeartbeat, now) })
            .ToList();
    }

    public async Task<Result> DeleteSessionAsync(ClaimsPrincipal actor, int id, CancellationToken ct = default)
    {
        if (!_MayDelete(actor))
            return _Denied();
        await serverAuth.DeleteSessionAsync(id, ct);
        return Result.Success();
    }

    // ── Runs (read only: the clients own them and push them back, see DestructiveActionCatalog.RunsHaveNoActions) ──
    /// <summary>Every run on this server as the Runs list shows it: grouped by <c>GroupCode</c>, a run without one on its
    /// own row, bounties added up and the fleet it was flown in inferred. Flat reads joined in memory, like the fleet
    /// list; the clients' push route does not pass through here.</summary>
    public async Task<IReadOnlyList<RunGroupListItem>> ListRunGroupsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<FleetListItem> fleets = await ListFleetItemsAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var runs = await db.Set<Run>().AsNoTracking()
            .Where(r => r.DeletedAtUtc == null)
            .Select(r => new RunRow(r.Id, r.CharacterId, r.GroupCode, r.CharacterNameSnapshot, r.SiteName, r.ActivityKind, r.SolarSystemId,
                r.State, r.StartedAtUtc, r.StoppedAtUtc, r.HomefrontOutcome, r.Revision, r.LastPushedAtUtc))
            .ToListAsync(ct);
        // Added up here: SQLite cannot sum a decimal column.
        var bountyRows = await db.Set<RunBountyEntry>().AsNoTracking().Select(b => new { b.RunId, b.Isk }).ToListAsync(ct);
        var bounties = bountyRows.GroupBy(b => b.RunId).ToDictionary(g => g.Key, g => g.Sum(b => b.Isk));

        return runs
            .GroupBy(r => r.GroupCode ?? r.Id.ToString("N"))
            .Select(g => _RunGroup(g.Key, g.OrderBy(r => r.StartedAtUtc).ToList(), bounties, fleets, now))
            .ToList();
    }

    /// <summary>The runs of one Runs-list row with everything they carry, read through the same split include chain as
    /// the sync pull. Empty when the group is gone.</summary>
    public async Task<IReadOnlyList<Run>> GetRunGroupRunsAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var runs = db.Set<Run>().AsNoTracking().Where(r => r.DeletedAtUtc == null);
        runs = Guid.TryParseExact(key, "N", out var runId)
            ? runs.Where(r => r.GroupCode == key || (r.GroupCode == null && r.Id == runId))
            : runs.Where(r => r.GroupCode == key);
        return await ServerRunSyncRepository.WithChildren(runs).OrderBy(r => r.StartedAtUtc).ToListAsync(ct);
    }

    private sealed record RunRow(
        Guid Id, long CharacterId, string? GroupCode, string? NameSnapshot, string? SiteName, ActivityKind Kind, int? SolarSystemId,
        RunState State, DateTime StartedAtUtc, DateTime? StoppedAtUtc, HomefrontOutcome? Outcome, int Revision, DateTime? LastPushedAtUtc);

    private static RunGroupListItem _RunGroup(
        string key, List<RunRow> runs, IReadOnlyDictionary<Guid, decimal> bounties, IReadOnlyList<FleetListItem> fleets, DateTimeOffset now)
    {
        var pilots = runs
            .Select(r => new RunPilot
            {
                RunId = r.Id,
                CharacterId = r.CharacterId,
                NameSnapshot = r.NameSnapshot,
                State = r.State,
                BountyIsk = bounties.GetValueOrDefault(r.Id),
            })
            .ToList();
        var first = runs[0];
        return new RunGroupListItem
        {
            Key = key,
            GroupCode = first.GroupCode,
            SiteName = runs.Select(r => r.SiteName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            Kind = first.Kind,
            SolarSystemId = runs.Select(r => r.SolarSystemId).FirstOrDefault(id => id is not null),
            Pilots = pilots,
            StartedAtUtc = first.StartedAtUtc,
            StoppedAtUtc = runs.Any(r => r.StoppedAtUtc is null) ? null : runs.Max(r => r.StoppedAtUtc),
            Outcome = runs.Select(r => r.Outcome).FirstOrDefault(o => o is not null),
            Revision = runs.Max(r => r.Revision),
            LastPushedAtUtc = runs.Max(r => r.LastPushedAtUtc),
            BountyIsk = pilots.Sum(p => p.BountyIsk),
            DerivedFleet = RunFleetMatch.Find(first.StartedAtUtc, pilots.Select(p => p.CharacterId).ToHashSet(), fleets, now),
        };
    }

    // ── Cross-entity reads ─────────────────────────────────────────────────────────────────────────
    public async Task<DataCounts> CountAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var compositionIds = db.Set<FleetComposition>().Select(c => (long?)c.Id);
        var liveRuns = db.Set<Run>().Where(r => r.DeletedAtUtc == null);
        return new DataCounts
        {
            Characters = await db.Set<SyncedCharacter>().CountAsync(ct),
            Fleets = await db.Set<Fleet>().CountAsync(ct),
            FleetsNeedingAttention = await db.Set<Fleet>()
                .CountAsync(f => f.FleetCompositionId != null && !compositionIds.Contains(f.FleetCompositionId), ct),
            Compositions = await db.Set<FleetComposition>().CountAsync(ct),
            SharedFits = await db.Set<SharedFit>().CountAsync(ct),
            RunGroups = await liveRuns.Where(r => r.GroupCode != null).Select(r => r.GroupCode).Distinct().CountAsync(ct),
            SoloRuns = await liveRuns.CountAsync(r => r.GroupCode == null, ct),
            Sessions = await db.Set<ServerSession>().CountAsync(ct),
        };
    }

    /// <summary>Every composition with its roles and entries, which entries still link to an existing shared fit, and
    /// the fleets coupled to it. Flat reads joined in memory.</summary>
    public async Task<IReadOnlyList<CompositionListItem>> ListCompositionItemsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<FleetListItem> fleets = await ListFleetItemsAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var all = await db.Set<FleetComposition>().AsNoTracking().ToListAsync(ct);
        var roles = await db.Set<FleetCompositionRole>().AsNoTracking().ToListAsync(ct);
        var entries = await db.Set<FleetCompositionEntry>().AsNoTracking().ToListAsync(ct);
        var fitIds = (await db.Set<SharedFit>().AsNoTracking().Select(f => f.Id).ToListAsync(ct)).ToHashSet();

        var rolesByComposition = roles.OrderBy(r => r.SortOrder).ToLookup(r => r.CompositionId);
        var entriesByRole = entries.OrderBy(e => e.SortOrder).ToLookup(e => e.RoleId);
        var fleetsByComposition = fleets
            .Where(f => f.Fleet.FleetCompositionId is not null)
            .ToLookup(f => f.Fleet.FleetCompositionId.GetValueOrDefault());
        return all
            .Select(c => new CompositionListItem
            {
                Composition = c,
                Roles = rolesByComposition[c.Id]
                    .Select(r => new CompositionRoleItem
                    {
                        Role = r,
                        Entries = entriesByRole[r.Id]
                            .Select(e => new CompositionEntryItem
                            {
                                Entry = e,
                                LinkedSharedFitId = e.Fit.ServerSharedFitId is { } id && fitIds.Contains(id) ? id : null,
                            })
                            .ToList(),
                    })
                    .ToList(),
                UsedBy = fleetsByComposition[c.Id].OrderBy(f => f.Status).ThenBy(f => f.Fleet.Name).ToList(),
            })
            .ToList();
    }

    /// <summary>Every shared fit with the compositions whose entries link to it and the fleet members who have it
    /// assigned. Both keep their own copy of the fit; this is only the link back to the library.</summary>
    public async Task<IReadOnlyList<SharedFitListItem>> ListSharedFitItemsAsync(CancellationToken ct = default)
    {
        var fits = await sharedFits.ListAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var links = await db.Set<FleetCompositionEntry>().AsNoTracking()
            .Where(e => e.Fit.ServerSharedFitId != null)
            .Join(db.Set<FleetCompositionRole>(), e => e.RoleId, r => r.Id, (e, r) => new { e.Fit.ServerSharedFitId, r.CompositionId, r.RoleName })
            .Join(db.Set<FleetComposition>(), l => l.CompositionId, c => c.Id,
                (l, c) => new { l.ServerSharedFitId, l.CompositionId, CompositionName = c.Name, l.RoleName })
            .ToListAsync(ct);
        var assigned = await db.Set<FleetMember>().AsNoTracking()
            .Where(m => m.AssignedFit != null && m.AssignedFit.ServerSharedFitId != null)
            .ToListAsync(ct);
        var fleetNames = await db.Set<Fleet>().AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);

        var usesByFit = links
            .GroupBy(l => l.ServerSharedFitId ?? 0)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(l => (l.CompositionId, l.CompositionName))
                .Select(c => new SharedFitCompositionUse
                {
                    CompositionId = c.Key.CompositionId,
                    CompositionName = c.Key.CompositionName,
                    RoleNames = c.Select(l => l.RoleName).Distinct().ToList(),
                })
                .OrderBy(c => c.CompositionName)
                .ToList());
        var assignmentsByFit = assigned
            .Where(m => m.AssignedFit?.ServerSharedFitId is not null)
            .ToLookup(m => m.AssignedFit?.ServerSharedFitId ?? 0, m => new SharedFitAssignment
            {
                FleetId = m.FleetId,
                FleetName = fleetNames.GetValueOrDefault(m.FleetId) ?? $"Fleet {m.FleetId}",
                CharacterId = m.CharacterId,
            });
        return fits
            .Select(f => new SharedFitListItem
            {
                Fit = f,
                Compositions = usesByFit.GetValueOrDefault(f.Id) ?? [],
                Assignments = assignmentsByFit[f.Id].ToList(),
            })
            .ToList();
    }

    /// <summary>Every paired character with its sessions, fleet seats, shared fits and owned compositions.</summary>
    public async Task<IReadOnlyList<CharacterListItem>> ListCharacterItemsAsync(CancellationToken ct = default)
    {
        var characters = await serverAuth.ListSyncedAsync(ct);
        var sessions = await ListSessionItemsAsync(ct);
        var fleets = await ListFleetItemsAsync(ct);
        var fits = await sharedFits.ListAsync(ct);
        var owned = await compositions.ListAllAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var seats = await db.Set<FleetMember>().AsNoTracking()
            .Select(m => new { m.FleetId, m.CharacterId, m.Role, m.ShipTypeId })
            .ToListAsync(ct);

        var fleetById = fleets.ToDictionary(f => f.Fleet.Id);
        var sessionsByCharacter = sessions.ToLookup(s => s.Session.SyncedCharacterId);
        var seatsByCharacter = seats.ToLookup(s => s.CharacterId);
        var fitsByCharacter = fits.ToLookup(f => f.SharedByCharacterId);
        var compositionsByOwner = owned.ToLookup(c => c.OwnerCharacterId);
        return characters
            .Select(c => new CharacterListItem
            {
                Character = c,
                Sessions = sessionsByCharacter[c.Id].OrderByDescending(s => s.Session.LastHeartbeat).ToList(),
                Fleets = seatsByCharacter[c.EsiCharacterId]
                    .Where(s => fleetById.ContainsKey(s.FleetId))
                    .Select(s => new CharacterFleetSeat { Fleet = fleetById[s.FleetId], Role = s.Role, ShipTypeId = s.ShipTypeId })
                    .OrderBy(s => s.Fleet.Status).ThenBy(s => s.Fleet.Fleet.Name)
                    .ToList(),
                SharedFits = fitsByCharacter[c.EsiCharacterId].OrderBy(f => f.Name).ToList(),
                OwnedCompositions = compositionsByOwner[c.EsiCharacterId].OrderBy(x => x.Name).ToList(),
            })
            .ToList();
    }

    private static bool _MayDelete(ClaimsPrincipal actor) => actor.HasPanelPermission(PanelPermissions.DataDelete);

    private static Result _Denied() => Result.Failure(new ResultMessage(
        MessageSeverity.Error, MessageCodes.PermissionDenied, "This needs the Data · Delete permission.", "Panel"));

    private static Result _NotFound(string what) => Result.Failure(new ResultMessage(
        MessageSeverity.Error, MessageCodes.NotFound, $"The {what} is already gone.", "Panel"));
}
