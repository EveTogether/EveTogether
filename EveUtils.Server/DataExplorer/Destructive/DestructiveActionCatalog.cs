using EveUtils.Server.Auth;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.DataExplorer.Destructive;

/// <summary>
/// The destructive actions each kind of record offers, with their consequences counted from the database when they are
/// asked for, so the confirm says what this record will take with it rather than what such a record usually does. The
/// panel asks again when an action is armed, so what an admin confirms is what the database held a moment ago.
/// The tier follows how much is lost: two steps where the rest of the server survives it, typing the name where it
/// cascades or leaves other records pointing at nothing.
/// </summary>
public sealed class DestructiveActionCatalog(
    IDbContextFactory<ServerDbContext> contextFactory,
    DataAdminService dataAdmin,
    TimeProvider clock) : IScopedService
{
    /// <summary>Why a run has no destructive actions, for the block that says so where runs are shown.</summary>
    public const string RunsHaveNoActions =
        "Runs belong to the pilots' clients, which push them here through the sync. Deleting one on the server would " +
        "only bring it back on the next push, so the panel shows runs and cannot delete them.";

    public async Task<IReadOnlyList<DestructiveAction>> ForFleetAsync(long fleetId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fleet = await db.Set<Fleet>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is null)
            return [];

        var members = await db.Set<FleetMember>().CountAsync(m => m.FleetId == fleetId, ct);
        var wingIds = db.Set<FleetWing>().Where(w => w.FleetId == fleetId).Select(w => w.Id);
        var wings = await wingIds.CountAsync(ct);
        var squads = await db.Set<FleetSquad>().CountAsync(s => wingIds.Contains(s.WingId), ct);
        var invites = await db.Set<FleetInvite>().CountAsync(i => i.FleetId == fleetId && i.Status == FleetInviteStatus.Pending, ct);
        var requests = await db.Set<FleetJoinRequest>().CountAsync(r => r.FleetId == fleetId && r.Status == FleetJoinRequestStatus.Pending, ct);
        var inOp = fleet.State == FleetState.Active && fleet.Activation == FleetActivation.Active;

        var purgeLoses = _List([
            _Count(wings, "wing", "wings"), _Count(squads, "squad", "squads"), _Count(members, "member", "members"),
            _Count(invites, "pending invite", "pending invites"), _Count(requests, "pending join request", "pending join requests")]);
        List<Consequence> purgeConsequences =
        [
            _Line(purgeLoses is null ? "Removes the fleet. It has no wings, squads, members or pending invites." : $"Removes the fleet with {purgeLoses}."),
        ];
        if (inOp)
            purgeConsequences.Add(_Warning("The fleet is in op right now."));
        purgeConsequences.Add(_Line("This cannot be undone."));

        var purge = new DestructiveAction
        {
            Title = "Purge now",
            Summary = fleet.State == FleetState.Archived
                ? $"Archived. The sweep deletes it {_Within(FleetCleanupOptions.Default.HardDeleteAfter - (clock.GetUtcNow() - fleet.LastActivityAt))} anyway."
                : "Raw delete that skips the disband. Wings, squads, members and invites go with it.",
            ButtonLabel = "Purge",
            Question = $"Purge “{fleet.Name}” permanently?",
            Consequences = purgeConsequences,
            ConfirmLabel = "Purge permanently",
            NameToType = fleet.Name,
            RunAsync = actor => dataAdmin.PurgeFleetAsync(actor, fleetId),
        };
        if (fleet.State == FleetState.Archived)
            return [purge];

        var disband = new DestructiveAction
        {
            Title = "Disband",
            Summary = "Archives the fleet, as its creator would. The sweep deletes it a day later.",
            ButtonLabel = "Disband",
            Question = $"Disband “{fleet.Name}”?",
            Consequences =
            [
                members == 0
                    ? _Line("Nobody is on the roster.")
                    : inOp
                        ? _Warning($"The fleet ends for its {_Count(members, "member", "members")} while it is in op.")
                        : _Line($"The fleet ends for its {_Count(members, "member", "members")}."),
                _Line("It moves to Archived, where it can still be purged or left for the sweep."),
            ],
            ConfirmLabel = "Disband fleet",
            Tone = DestructiveTone.Recoverable,
            RunAsync = actor => dataAdmin.DisbandFleetAsync(actor, fleetId),
        };
        return [disband, purge];
    }

    public async Task<IReadOnlyList<DestructiveAction>> ForCharacterAsync(int syncedCharacterId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var character = await db.Set<SyncedCharacter>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == syncedCharacterId, ct);
        if (character is null)
            return [];

        var esiId = character.EsiCharacterId;
        // Heartbeats are compared in memory: not every provider translates a DateTimeOffset comparison.
        var heartbeats = await db.Set<ServerSession>()
            .Where(s => s.SyncedCharacterId == syncedCharacterId).Select(s => s.LastHeartbeat).ToListAsync(ct);
        var sessions = heartbeats.Count;
        var liveSessions = heartbeats.Count(h => clock.GetUtcNow() - h <= ServerSessionService.LiveWindow);
        var fleets = await db.Set<FleetMember>().Where(m => m.CharacterId == esiId).Select(m => m.FleetId).Distinct().CountAsync(ct);
        var fits = await db.Set<SharedFit>().CountAsync(f => f.SharedByCharacterId == esiId, ct);
        var compositions = await db.Set<FleetComposition>().CountAsync(c => c.OwnerCharacterId == esiId, ct);
        var runGroups = await db.Set<Run>()
            .Where(r => r.CharacterId == esiId && r.DeletedAtUtc == null && r.GroupCode != null)
            .Select(r => r.GroupCode).Distinct().CountAsync(ct);

        var sessionLine = sessions == 0
            ? _Line("It has no sessions.")
            : liveSessions == 0
                ? _Line($"{_Count(sessions, "session is", "sessions are")} removed. {(sessions == 1 ? "It is not live." : "None of them is live.")}")
                : _Warning($"{_Count(sessions, "session is", "sessions are")} removed, {(sessions == 1 ? "and it is" : $"{liveSessions} of them")} live. " +
                           (liveSessions == 1 ? "That client is signed out and has to pair again." : "Those clients are signed out and have to pair again."));
        var leftBehind = _List([
            _Count(fleets, "fleet membership", "fleet memberships"), _Count(fits, "shared fit", "shared fits"),
            _Count(runGroups, "run group", "run groups"), _Count(compositions, "composition", "compositions")]);

        return
        [
            new DestructiveAction
            {
                Title = "Delete paired character",
                Summary = "Removes the pairing and its sessions. Other records keep the character id.",
                ButtonLabel = "Delete",
                Question = $"Delete paired character “{character.CharacterName}”?",
                Consequences =
                [
                    sessionLine,
                    leftBehind is null
                        ? _Line("No fleet, shared fit, run or composition refers to it.")
                        : _Warning($"{leftBehind} keep pointing at a character that is no longer paired."),
                    _Line("The pilot has to pair again from the client to come back."),
                ],
                ConfirmLabel = "Delete character",
                NameToType = character.CharacterName,
                RunAsync = actor => dataAdmin.DeleteSyncedCharacterAsync(actor, syncedCharacterId),
            },
        ];
    }

    public async Task<IReadOnlyList<DestructiveAction>> ForCompositionAsync(long compositionId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var composition = await db.Set<FleetComposition>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == compositionId, ct);
        if (composition is null)
            return [];

        var roleIds = db.Set<FleetCompositionRole>().Where(r => r.CompositionId == compositionId).Select(r => r.Id);
        var roles = await roleIds.CountAsync(ct);
        var entries = await db.Set<FleetCompositionEntry>().CountAsync(e => roleIds.Contains(e.RoleId), ct);
        var skillMinimums = await db.Set<FleetCompositionEntry>().Where(e => roleIds.Contains(e.RoleId))
            .SelectMany(e => e.SkillMinimums).CountAsync(ct);
        var usedBy = await db.Set<Fleet>().AsNoTracking()
            .Where(f => f.FleetCompositionId == compositionId)
            .OrderBy(f => f.Name).Select(f => f.Name).ToListAsync(ct);
        var cascade = _List([_Count(roles, "role", "roles"), _Count(entries, "entry", "entries"),
            _Count(skillMinimums, "skill minimum", "skill minimums")]);

        return
        [
            new DestructiveAction
            {
                Title = "Delete composition",
                Summary = "Hard delete. Its roles, entries and skill minimums go with it.",
                ButtonLabel = "Delete",
                Question = $"Delete composition “{composition.Name}”?",
                Consequences =
                [
                    _Line(cascade is null
                        ? "It has no roles yet."
                        : $"{cascade} {(roles + entries + skillMinimums == 1 ? "is" : "are")} deleted with it."),
                    usedBy.Count == 0
                        ? _Line("No fleet uses it.")
                        : _Warning($"{_Names(usedBy)} {(usedBy.Count == 1 ? "keeps" : "keep")} pointing at it. There is no foreign key, " +
                                   "so the fleet shows its composition as missing."),
                    _Line("Shared fits are not touched."),
                ],
                ConfirmLabel = "Delete composition",
                NameToType = usedBy.Count == 0 ? null : composition.Name,
                RunAsync = actor => dataAdmin.DeleteFleetCompositionAsync(actor, compositionId),
            },
        ];
    }

    public async Task<IReadOnlyList<DestructiveAction>> ForSharedFitAsync(int sharedFitId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var fit = await db.Set<SharedFit>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == sharedFitId, ct);
        if (fit is null)
            return [];

        var linkingCompositions = await db.Set<FleetCompositionEntry>().AsNoTracking()
            .Where(e => e.Fit.ServerSharedFitId == sharedFitId)
            .Join(db.Set<FleetCompositionRole>(), e => e.RoleId, r => r.Id, (e, r) => r.CompositionId)
            .Join(db.Set<FleetComposition>(), id => id, c => c.Id, (id, c) => c.Name)
            .Distinct().OrderBy(n => n).ToListAsync(ct);
        var assignedPilots = await db.Set<FleetMember>()
            .CountAsync(m => m.AssignedFit != null && m.AssignedFit.ServerSharedFitId == sharedFitId, ct);

        return
        [
            new DestructiveAction
            {
                Title = "Delete from the shared library",
                Summary = "Compositions and pilots that use it keep their own copy of the fit.",
                ButtonLabel = "Delete",
                Question = $"Delete shared fit “{fit.Name}”?",
                Consequences =
                [
                    linkingCompositions.Count == 0
                        ? _Line("No composition links to it.")
                        : _Line($"{_Names(linkingCompositions)} {(linkingCompositions.Count == 1 ? "keeps its" : "keep their")} copy, " +
                                "but the link to the library shows as broken."),
                    assignedPilots == 0
                        ? _Line("No pilot has it assigned.")
                        : _Line($"{_Count(assignedPilots, "assigned pilot keeps", "assigned pilots keep")} their copy."),
                    _Line("Clients stop seeing it in the shared list."),
                ],
                ConfirmLabel = "Delete fit",
                RunAsync = actor => dataAdmin.DeleteSharedFitAsync(actor, sharedFitId),
            },
        ];
    }

    public async Task<IReadOnlyList<DestructiveAction>> ForSessionAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var session = await db.Set<ServerSession>().AsNoTracking()
            .Include(s => s.SyncedCharacter)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
            return [];

        var others = await db.Set<ServerSession>().CountAsync(s => s.SyncedCharacterId == session.SyncedCharacterId && s.Id != sessionId, ct);
        var silence = clock.GetUtcNow() - session.LastHeartbeat;
        var owner = session.SyncedCharacter?.CharacterName ?? $"Character #{session.SyncedCharacterId}";

        return
        [
            new DestructiveAction
            {
                Title = "Revoke session",
                Summary = "Deletes this one session. The character's other machines keep theirs.",
                ButtonLabel = "Revoke",
                Question = $"Revoke {owner}'s session issued {session.IssuedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC?",
                Consequences =
                [
                    silence <= ServerSessionService.LiveWindow
                        ? _Warning("This session is live: the machine holding it is connected now and has to pair again.")
                        : _Line($"Silent for {DurationText.Coarse(silence)}. Nothing appears to be using it."),
                    _Line(others == 0
                        ? "It is the character's only session."
                        : $"{_Count(others, "other session", "other sessions")} of this character {(others == 1 ? "stays" : "stay")}."),
                ],
                ConfirmLabel = "Revoke session",
                Tone = DestructiveTone.Recoverable,
                RunAsync = actor => dataAdmin.DeleteSessionAsync(actor, sessionId),
            },
        ];
    }

    private static Consequence _Line(string text) => new() { Text = text };

    private static Consequence _Warning(string text) => new() { Text = text, Weight = ConsequenceWeight.Warning };

    private static string? _Count(int count, string one, string many) => count switch
    {
        0 => null,
        1 => $"1 {one}",
        _ => $"{count} {many}",
    };

    /// <summary>"a", "a and b", "a, b and c" over the parts that are not null; null when none are.</summary>
    private static string? _List(IEnumerable<string?> parts)
    {
        var present = parts.OfType<string>().ToList();
        return present.Count switch
        {
            0 => null,
            1 => present[0],
            _ => $"{string.Join(", ", present[..^1])} and {present[^1]}",
        };
    }

    private static string _Names(IReadOnlyList<string> names) =>
        _List(names.Select(n => $"“{n}”")) ?? string.Empty;

    private static string _Within(TimeSpan left) =>
        left <= TimeSpan.Zero ? "on its next pass" : $"in {DurationText.Coarse(left)}";
}
