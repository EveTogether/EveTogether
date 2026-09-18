using EveUtils.Server.Auth;
using EveUtils.Server.Components;
using EveUtils.Server.Components.Fleets;
using EveUtils.Server.Esi;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// The Dashboard's reads. The overview is one fixed set of flat projections — one per table, joined in memory — so its
/// cost does not depend on how many characters or fleets exist; a query per tile or per attention item would. The totals
/// come from <see cref="DataAdminService.CountAsync"/>, so a tile and the sidebar entry beside it can never disagree.
/// </summary>
public sealed class DashboardOverviewService(
    IDbContextFactory<ServerDbContext> contextFactory,
    DataAdminService dataAdmin,
    EsiNameLookup nameLookup,
    TimeProvider clock) : IScopedService
{
    private sealed record CharacterRow(int Id, int EsiCharacterId, string Name, DateTimeOffset PairedAt, int FailureCount, DateTimeOffset? LastFailedAt);
    private sealed record SessionRow(int Id, int SyncedCharacterId, DateTimeOffset LastHeartbeat);
    private sealed record MemberRow(long FleetId, int CharacterId);
    private sealed record EntryRow(long CompositionId, string FitName, int? ServerSharedFitId);
    private sealed record FitRow(int Id, int SharedByCharacterId, string SharedByCharacterName);
    private sealed record RunCharacterRow(long CharacterId, int Runs);

    public async Task<DashboardOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DataCounts counts = await dataAdmin.CountAsync(ct);

        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(ct);
        List<CharacterRow> characters = await db.Set<SyncedCharacter>().AsNoTracking()
            .Select(c => new CharacterRow(c.Id, c.EsiCharacterId, c.CharacterName, c.PairedAt, c.FailureCount, c.LastFailedAt))
            .ToListAsync(ct);
        List<SessionRow> sessions = await db.Set<ServerSession>().AsNoTracking()
            .Select(s => new SessionRow(s.Id, s.SyncedCharacterId, s.LastHeartbeat))
            .ToListAsync(ct);
        List<Fleet> fleets = await db.Set<Fleet>().AsNoTracking().ToListAsync(ct);
        List<MemberRow> members = await db.Set<FleetMember>().AsNoTracking()
            .Select(m => new MemberRow(m.FleetId, m.CharacterId))
            .ToListAsync(ct);
        Dictionary<long, string> compositions = await db.Set<FleetComposition>().AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        List<EntryRow> entries = await db.Set<FleetCompositionEntry>().AsNoTracking()
            .Join(db.Set<FleetCompositionRole>(), e => e.RoleId, r => r.Id,
                (e, r) => new EntryRow(r.CompositionId, e.Fit.FitName, e.Fit.ServerSharedFitId))
            .ToListAsync(ct);
        List<FitRow> fits = await db.Set<SharedFit>().AsNoTracking()
            .Select(f => new FitRow(f.Id, f.SharedByCharacterId, f.SharedByCharacterName))
            .ToListAsync(ct);
        List<RunCharacterRow> runsByCharacter = await db.Set<Run>().AsNoTracking()
            .Where(r => r.DeletedAtUtc == null)
            .GroupBy(r => r.CharacterId)
            .Select(g => new RunCharacterRow(g.Key, g.Count()))
            .ToListAsync(ct);

        HashSet<int> paired = characters.Select(c => c.EsiCharacterId).ToHashSet();
        HashSet<int> fitIds = fits.Select(f => f.Id).ToHashSet();
        Dictionary<int, int> sessionCountByCharacter = sessions.GroupBy(s => s.SyncedCharacterId).ToDictionary(g => g.Key, g => g.Count());
        HashSet<int> liveCharacterIds = sessions
            .Where(s => now - s.LastHeartbeat <= ServerSessionService.LiveWindow)
            .Select(s => s.SyncedCharacterId)
            .ToHashSet();

        List<AttentionItem> attention = [];
        attention.AddRange(_FailingRefreshes(characters, now));
        attention.AddRange(await _UnpairedReferencesAsync(paired, members, fits, runsByCharacter, ct));
        attention.AddRange(_MissingCompositions(fleets, compositions));
        attention.AddRange(_MissingFits(entries, compositions, fitIds));
        attention.AddRange(_LapsedSessions(sessions, characters, now));
        attention.AddRange(_SilentFleets(fleets, members, now));
        attention.AddRange(_ArchivedFleets(fleets, now));
        attention.AddRange(_NeverConnected(characters, sessionCountByCharacter, now));
        List<AttentionItem> ordered = attention.OrderBy(a => a.Severity).ThenBy(a => a.Kind).ToList();

        int needsAttention(DataEntity entity) =>
            ordered.Count(a => a.Entity == entity && a.Severity != AttentionSeverity.Info);

        int failing = characters.Count(c => c.FailureCount > 0);
        int neverConnected = characters.Count(c => c.FailureCount == 0 && !sessionCountByCharacter.ContainsKey(c.Id));
        int live = characters.Count(c => c.FailureCount == 0 && liveCharacterIds.Contains(c.Id));
        int idle = characters.Count - failing - neverConnected - live;

        Dictionary<FleetPanelStatus, int> fleetsByStatus = fleets
            .GroupBy(FleetListItem.StatusOf)
            .ToDictionary(g => g.Key, g => g.Count());
        int fleetsOf(FleetPanelStatus status) => fleetsByStatus.GetValueOrDefault(status);

        HashSet<long> compositionsInUse = fleets.Select(f => f.FleetCompositionId).OfType<long>()
            .Where(compositions.ContainsKey).ToHashSet();
        int fitsInComposition = entries.Select(e => e.ServerSharedFitId).OfType<int>().Where(fitIds.Contains).Distinct().Count();

        int liveSessions = sessions.Count(s => now - s.LastHeartbeat <= ServerSessionService.LiveWindow);
        int lapsing = sessions.Count(s => now - s.LastHeartbeat >= ServerSessionService.IdleLifetime);

        return new DashboardOverview
        {
            Characters = new OverviewTile
            {
                Total = counts.Characters,
                NeedsAttention = needsAttention(DataEntity.Characters),
                Segments =
                [
                    new("live", live, SegmentTone.Good),
                    new("idle", idle, SegmentTone.Neutral),
                    new("never connected", neverConnected, SegmentTone.Warning),
                    new("failing", failing, SegmentTone.Bad),
                ],
            },
            Fleets = new OverviewTile
            {
                Total = counts.Fleets,
                NeedsAttention = needsAttention(DataEntity.Fleets),
                Segments =
                [
                    new("in op", fleetsOf(FleetPanelStatus.InOp), SegmentTone.Good),
                    new("forming", fleetsOf(FleetPanelStatus.Forming), SegmentTone.Accent),
                    new("concluded", fleetsOf(FleetPanelStatus.Concluded), SegmentTone.Neutral),
                    new("archived", fleetsOf(FleetPanelStatus.Archived), SegmentTone.Neutral),
                ],
            },
            Compositions = new OverviewTile
            {
                Total = counts.Compositions,
                NeedsAttention = needsAttention(DataEntity.Compositions),
                Segments =
                [
                    new("in use", compositionsInUse.Count, SegmentTone.Accent),
                    new("unused", compositions.Count - compositionsInUse.Count, SegmentTone.Neutral),
                ],
            },
            SharedFits = new OverviewTile
            {
                Total = counts.SharedFits,
                NeedsAttention = needsAttention(DataEntity.SharedFits),
                Segments =
                [
                    new("in a composition", fitsInComposition, SegmentTone.Accent),
                    new("unused", fits.Count - fitsInComposition, SegmentTone.Neutral),
                ],
            },
            RunGroups = new OverviewTile
            {
                Total = counts.RunRows,
                NeedsAttention = needsAttention(DataEntity.Runs),
                Segments =
                [
                    new("in a group", counts.RunGroups, SegmentTone.Accent),
                    new("solo", counts.SoloRuns, SegmentTone.Neutral),
                ],
            },
            Sessions = new OverviewTile
            {
                Total = counts.Sessions,
                NeedsAttention = needsAttention(DataEntity.Sessions),
                Segments =
                [
                    new("live", liveSessions, SegmentTone.Good),
                    new("idle", sessions.Count - liveSessions - lapsing, SegmentTone.Neutral),
                    new("lapsing", lapsing, SegmentTone.Warning),
                ],
            },
            Attention = ordered,
        };
    }

    /// <summary>Fleets that are forming or in op, in-op first then newest activity, with their members' ships. Three flat
    /// reads joined in memory, like the fleet list.</summary>
    public async Task<RightNowSnapshot> GetRightNowAsync(CancellationToken ct = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(ct);
        List<Fleet> fleets = (await db.Set<Fleet>().AsNoTracking()
                .Where(f => f.State == FleetState.Active && f.Activation != FleetActivation.Concluded)
                .ToListAsync(ct))
            .OrderBy(f => f.Activation == FleetActivation.Active ? 0 : 1)
            .ThenByDescending(f => f.LastActivityAt)
            .ToList();
        List<long> fleetIds = fleets.Select(f => f.Id).ToList();
        var members = await db.Set<FleetMember>().AsNoTracking()
            .Where(m => fleetIds.Contains(m.FleetId))
            .Select(m => new { m.FleetId, m.CharacterId, m.ShipTypeId })
            .ToListAsync(ct);
        Dictionary<long, string> compositionNames = await db.Set<FleetComposition>().AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        Dictionary<long, string> names = await db.Set<SyncedCharacter>().AsNoTracking()
            .ToDictionaryAsync(c => (long)c.EsiCharacterId, c => c.CharacterName, ct);

        foreach ((long id, string name) in await nameLookup.ResolveAsync(
                     members.Select(m => (long)m.CharacterId).Where(id => !names.ContainsKey(id)).Distinct(), ct))
            names[id] = name;

        ILookup<long, int> characterIdsByFleet = members.ToLookup(m => m.FleetId, m => m.CharacterId);
        return new RightNowSnapshot
        {
            Names = names,
            Fleets = fleets
                .Select(f => new RightNowFleet
                {
                    Id = f.Id,
                    Name = f.Name,
                    Status = FleetListItem.StatusOf(f),
                    LastActivityAt = f.LastActivityAt,
                    MemberCharacterIds = characterIdsByFleet[f.Id].ToList(),
                    Ships = members
                        .Where(m => m.FleetId == f.Id && m.ShipTypeId is not null)
                        .GroupBy(m => m.ShipTypeId.GetValueOrDefault())
                        .OrderByDescending(g => g.Count())
                        .Select(g => new KeyValuePair<int, int>(g.Key, g.Count()))
                        .ToList(),
                    CompositionName = f.FleetCompositionId is { } id ? compositionNames.GetValueOrDefault(id) : null,
                })
                .ToList(),
        };
    }

    private static IEnumerable<AttentionItem> _FailingRefreshes(IReadOnlyList<CharacterRow> characters, DateTimeOffset now) =>
        characters.Where(c => c.FailureCount > 0).Select(c => new AttentionItem
        {
            Kind = AttentionKind.EsiRefreshFailing,
            Severity = AttentionSeverity.Problem,
            Entity = DataEntity.Characters,
            Title = c.FailureCount == ServerTokenRefreshService.RevokedFailureCount
                ? $"{c.Name}: ESI refused the refresh token"
                : $"{c.Name}: ESI token refresh is failing",
            Detail = (c.FailureCount == ServerTokenRefreshService.RevokedFailureCount
                    ? "The token was revoked. "
                    : $"{c.FailureCount} failures, the last one {RelativeTime.Label(c.LastFailedAt, now)}. ")
                + "The server cannot act for this character until they pair again.",
            Href = DataLinks.Character(c.EsiCharacterId),
        });

    private async Task<IEnumerable<AttentionItem>> _UnpairedReferencesAsync(
        IReadOnlySet<int> paired, IReadOnlyList<MemberRow> members, IReadOnlyList<FitRow> fits,
        IReadOnlyList<RunCharacterRow> runsByCharacter, CancellationToken ct)
    {
        Dictionary<long, List<long>> fleetsByCharacter = members
            .Where(m => !paired.Contains(m.CharacterId))
            .GroupBy(m => (long)m.CharacterId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.FleetId).Distinct().ToList());
        Dictionary<long, List<FitRow>> fitsByCharacter = fits
            .Where(f => !paired.Contains(f.SharedByCharacterId))
            .GroupBy(f => (long)f.SharedByCharacterId)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<long, int> runsByOrphan = runsByCharacter
            .Where(r => r.CharacterId > int.MaxValue || !paired.Contains((int)r.CharacterId))
            .ToDictionary(r => r.CharacterId, r => r.Runs);

        List<long> orphanIds = fleetsByCharacter.Keys.Union(fitsByCharacter.Keys).Union(runsByOrphan.Keys).Order().ToList();
        if (orphanIds.Count == 0)
            return [];

        IReadOnlyDictionary<long, string> resolved = await nameLookup.ResolveAsync(orphanIds, ct);
        return orphanIds.Select(id =>
        {
            List<long> fleetIds = fleetsByCharacter.GetValueOrDefault(id) ?? [];
            List<FitRow> sharedFits = fitsByCharacter.GetValueOrDefault(id) ?? [];
            int runs = runsByOrphan.GetValueOrDefault(id);
            string? name = resolved.GetValueOrDefault(id) ?? sharedFits.Select(f => f.SharedByCharacterName).FirstOrDefault(n => n.Length > 0);
            string references = string.Join(", ", new[]
            {
                fleetIds.Count > 0 ? _Plural(fleetIds.Count, "fleet roster") : null,
                sharedFits.Count > 0 ? _Plural(sharedFits.Count, "shared fit") : null,
                runs > 0 ? _Plural(runs, "run") : null,
            }.OfType<string>());

            // The link opens the first record that has a list to land on; the counts in the detail say how many more there are.
            (DataEntity entity, string href) = sharedFits.Count > 0 ? (DataEntity.SharedFits, DataLinks.SharedFit(sharedFits[0].Id))
                : fleetIds.Count > 0 ? (DataEntity.Fleets, DataLinks.Fleet(fleetIds[0]))
                : (DataEntity.Runs, DataLinks.RunsOf(id));
            return new AttentionItem
            {
                Kind = AttentionKind.ReferencesUnpairedCharacter,
                Severity = AttentionSeverity.Warning,
                Entity = entity,
                Title = $"{name ?? "An unknown character"} is referenced but not paired",
                Detail = $"Character id {id}: {references}. Left behind when a paired character was removed.",
                Href = href,
            };
        }).ToList();
    }

    private static IEnumerable<AttentionItem> _MissingCompositions(IReadOnlyList<Fleet> fleets, IReadOnlyDictionary<long, string> compositions) =>
        fleets.Where(f => f.FleetCompositionId is { } id && !compositions.ContainsKey(id)).Select(f => new AttentionItem
        {
            Kind = AttentionKind.FleetCompositionMissing,
            Severity = AttentionSeverity.Warning,
            Entity = DataEntity.Fleets,
            Title = $"Fleet “{f.Name}” points at a composition that does not exist",
            Detail = $"FleetCompositionId {f.FleetCompositionId} has no row. There is no foreign key, so deleting the composition left the reference behind.",
            Href = DataLinks.Fleet(f.Id),
        });

    private static IEnumerable<AttentionItem> _MissingFits(
        IReadOnlyList<EntryRow> entries, IReadOnlyDictionary<long, string> compositions, IReadOnlySet<int> fitIds) =>
        entries
            .Where(e => e.ServerSharedFitId is { } id && !fitIds.Contains(id))
            .GroupBy(e => e.CompositionId)
            .Select(g => new AttentionItem
            {
                Kind = AttentionKind.CompositionFitMissing,
                Severity = AttentionSeverity.Warning,
                Entity = DataEntity.Compositions,
                Title = $"Composition “{compositions.GetValueOrDefault(g.Key) ?? "?"}” links to a deleted shared fit",
                Detail = $"{string.Join(", ", g.Select(e => $"“{e.FitName}” → ServerSharedFitId {e.ServerSharedFitId}"))}. "
                    + "The entry keeps its own copy of the fit, so only the link is broken.",
                Href = DataLinks.Composition(g.Key),
            });

    private static IEnumerable<AttentionItem> _LapsedSessions(
        IReadOnlyList<SessionRow> sessions, IReadOnlyList<CharacterRow> characters, DateTimeOffset now)
    {
        Dictionary<int, string> nameById = characters.ToDictionary(c => c.Id, c => c.Name);
        return sessions.Where(s => now - s.LastHeartbeat >= ServerSessionService.IdleLifetime).Select(s => new AttentionItem
        {
            Kind = AttentionKind.SessionPastIdleLifetime,
            Severity = AttentionSeverity.Warning,
            Entity = DataEntity.Sessions,
            Title = $"A session is past the {ServerSessionService.IdleLifetime.TotalDays:0}-day idle window",
            Detail = $"{nameById.GetValueOrDefault(s.SyncedCharacterId) ?? "Unknown character"}, session {s.Id}, silent for {(now - s.LastHeartbeat).TotalDays:0} days. The next cleanup sweep removes it.",
            Href = DataLinks.Session(s.Id),
        });
    }

    private static IEnumerable<AttentionItem> _SilentFleets(IReadOnlyList<Fleet> fleets, IReadOnlyList<MemberRow> members, DateTimeOffset now)
    {
        Dictionary<long, int> memberCounts = members.GroupBy(m => m.FleetId).ToDictionary(g => g.Key, g => g.Count());
        return fleets
            .Where(f => FleetListItem.StatusOf(f) != FleetPanelStatus.Archived && now - f.LastActivityAt > FleetListItem.StaleAfter)
            .Select(f => new AttentionItem
            {
                Kind = AttentionKind.FleetSilent,
                Severity = AttentionSeverity.Info,
                Entity = DataEntity.Fleets,
                Title = $"Fleet “{f.Name}” has been silent for {(now - f.LastActivityAt).TotalDays:0} days",
                Detail = $"Still {FleetStatusPill.Label(FleetListItem.StatusOf(f)).ToLowerInvariant()}, with {_Plural(memberCounts.GetValueOrDefault(f.Id), "member")} holding a slot.",
                Href = DataLinks.Fleet(f.Id),
            });
    }

    // LastActivityAt is stamped at archive time, so it doubles as the archived-at clock, exactly as the sweep reads it.
    private static IEnumerable<AttentionItem> _ArchivedFleets(IReadOnlyList<Fleet> fleets, DateTimeOffset now) =>
        fleets.Where(f => FleetListItem.StatusOf(f) == FleetPanelStatus.Archived).Select(f =>
        {
            TimeSpan left = FleetCleanupOptions.Default.HardDeleteAfter - (now - f.LastActivityAt);
            return new AttentionItem
            {
                Kind = AttentionKind.FleetArchived,
                Severity = AttentionSeverity.Info,
                Entity = DataEntity.Fleets,
                Title = $"Fleet “{f.Name}” is archived",
                Detail = left > TimeSpan.Zero
                    ? $"The cleanup sweep deletes it in {Math.Ceiling(left.TotalHours):0}h."
                    : "It is past its keep window; the next cleanup sweep deletes it.",
                Href = DataLinks.Fleet(f.Id),
            };
        });

    private static IEnumerable<AttentionItem> _NeverConnected(
        IReadOnlyList<CharacterRow> characters, IReadOnlyDictionary<int, int> sessionCounts, DateTimeOffset now) =>
        characters.Where(c => c.FailureCount == 0 && !sessionCounts.ContainsKey(c.Id)).Select(c => new AttentionItem
        {
            Kind = AttentionKind.CharacterNeverConnected,
            Severity = AttentionSeverity.Info,
            Entity = DataEntity.Characters,
            Title = $"{c.Name} paired but has no session",
            Detail = $"Paired {RelativeTime.Label(c.PairedAt, now)} and no client has connected for them since.",
            Href = DataLinks.Character(c.EsiCharacterId),
        });

    private static string _Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
