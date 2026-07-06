using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Fleet;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Client.Esi;

/// <summary>
/// Client-side orchestration for FC control of the coupled in-game fleet. ESI writes run on the client
/// (the desktop app holds the boss's token), so this calls the ESI write through the metered pipeline and, where it
/// owns the change, mirrors it locally + broadcasts a refresh. Every write is source-agnostic: it works for a
/// client-only and a server fleet alike, taking the coupled in-game ids from the caller and reading the plan through
/// <see cref="IFleetClient"/>; the in-game wing/squad ids are resolved from the live fleet by name (never stored), so
/// no migration is needed and the push is idempotent + self-healing. The writes cover MOTD/free-move, member
/// move/kick, the wing/squad structure and the roster invite — all on the same seam. Singleton.
/// </summary>
public sealed class FleetEsiControlService(
    IEsiFleetClient fleetClient,
    IFleetRepository repository,
    IEventBus eventBus) : ISingletonService
{
    /// <summary>
    /// Sets the live fleet's MOTD and/or free-move from EVE Together (boss token, ESI-write + precheck). The caller
    /// supplies the coupled in-game ids (the VM holds them from the fleet header, which the server only relays) so this
    /// works for both client-only and server fleets without resolving the link from the local repository. Mirrors the
    /// change onto the internal entity only when it lives in the client repository (a client-only fleet); null fields
    /// are left unchanged.
    /// </summary>
    public async Task<Result> SetFleetSettingsAsync(long internalFleetId, long esiFleetId, int bossCharacterId, string? motd, bool? isFreeMove,
        CancellationToken cancellationToken = default)
    {
        var write = await fleetClient.SetFleetSettingsAsync(esiFleetId, bossCharacterId, motd, isFreeMove, cancellationToken);
        if (!write.IsSuccess)
            return write.ToResult("Fleet");

        // Mirror onto the local entity only for a client-only fleet — a server fleet isn't in the client repository
        // (the in-game MOTD is already set via ESI above, and the server holds its own copy).
        if (await repository.GetAsync(internalFleetId, cancellationToken) is { } fleet)
        {
            if (motd is not null)
                fleet.Motd = motd;
            if (isFreeMove is { } freeMove)
                fleet.IsFreeMove = freeMove;
            await repository.UpdateAsync(fleet, cancellationToken);
        }

        await BroadcastRosterChangedAsync(internalFleetId, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Pushes a member's roster position to the live fleet: translates our internal role + wing/squad into the
    /// in-game ESI ids (resolved from the live fleet by name, like the invite) and moves the member there
    /// (<c>PUT /fleets/{id}/members/{member}/</c>). The internal roster move stays in the VM's normal move path; this
    /// only mirrors the accepted plan to ESI and does not broadcast — the internal move already refreshed the roster,
    /// and the 5s sync reconciles the in-game side. Fails clearly when the target wing/squad has not been pushed to the
    /// in-game fleet yet (run <see cref="ApplyFleetStructureAsync"/> first). Source-agnostic: works for a server fleet.
    /// </summary>
    public async Task<Result> MoveMemberAsync(IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId,
        int memberCharacterId, FleetRole role, long wingId, long squadId, CancellationToken cancellationToken = default)
    {
        var resolver = await _BuildEsiPositionResolverAsync(fleets, fleetId, esiFleetId, bossCharacterId, cancellationToken);
        if (!resolver.IsSuccess)
            return Result.Failure([.. resolver.Messages]);

        if (resolver.Value!.Resolve(role, wingId, squadId) is not { } position)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This member's wing/squad isn't in the in-game fleet yet — push the structure first.", "Fleet"));

        var write = await fleetClient.MoveMemberAsync(
            esiFleetId, memberCharacterId, position.Role, position.WingId, position.SquadId, bossCharacterId, cancellationToken);
        return write.IsSuccess ? Result.Success() : write.ToResult("Fleet");
    }

    /// <summary>Kicks a member from the live fleet (<c>DELETE /fleets/{id}/members/{member}/</c>), keyed by character
    /// id so it can run after the internal removal. Mirrors the internal kick to ESI only — no broadcast (the internal
    /// removal already refreshed the roster). Source-agnostic: only the coupled in-game ids are needed, so it works for a
    /// server fleet.</summary>
    public async Task<Result> KickMemberAsync(long esiFleetId, int bossCharacterId, int memberCharacterId,
        CancellationToken cancellationToken = default)
    {
        var write = await fleetClient.KickMemberAsync(esiFleetId, memberCharacterId, bossCharacterId, cancellationToken);
        return write.IsSuccess ? Result.Success() : write.ToResult("Fleet");
    }

    /// <summary>
    /// Pushes our planned wing/squad structure to the live fleet. Reads our structure through <paramref name="fleets"/>
    /// (so it works for a client-only or a server fleet) and matches it to the live in-game wings/squads by name, creating
    /// only what is missing and renaming it to match — idempotent and self-healing (the in-game ids are read live, never
    /// stored). A rename failure is a warning (the unit exists); a create failure stops and is returned so a re-run resumes.
    /// </summary>
    public async Task<Result> ApplyFleetStructureAsync(IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId,
        CancellationToken cancellationToken = default)
    {
        var live = await fleetClient.GetWingsAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!live.IsSuccess || live.Value is null)
            return live.ToResult("Fleet");

        var wingsByName = new Dictionary<string, (long EsiWingId, HashSet<string> SquadNames)>(StringComparer.Ordinal);
        foreach (var liveWing in live.Value)
            wingsByName[liveWing.Name] = (liveWing.Id, [.. liveWing.Squads.Select(squad => squad.Name)]);

        var warnings = new List<ResultMessage>();
        foreach (var wing in await fleets.ListWingsAsync(fleetId))
        {
            if (!wingsByName.TryGetValue(wing.Name, out var inGameWing))
            {
                var created = await fleetClient.CreateWingAsync(esiFleetId, bossCharacterId, cancellationToken);
                if (!created.IsSuccess)
                    return Result.Failure(created.Error!.ToResultMessage("Fleet"));
                var renamed = await fleetClient.RenameWingAsync(esiFleetId, created.Value, wing.Name, bossCharacterId, cancellationToken);
                if (!renamed.IsSuccess)
                    warnings.Add(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed,
                        $"Wing '{wing.Name}' was created in-game but could not be renamed.", "Fleet"));
                inGameWing = (created.Value, []);
                wingsByName[wing.Name] = inGameWing;
            }

            foreach (var squad in await fleets.ListSquadsAsync(wing.Id))
            {
                if (inGameWing.SquadNames.Contains(squad.Name))
                    continue;
                var created = await fleetClient.CreateSquadAsync(esiFleetId, inGameWing.EsiWingId, bossCharacterId, cancellationToken);
                if (!created.IsSuccess)
                    return Result.Failure(created.Error!.ToResultMessage("Fleet"));
                var renamed = await fleetClient.RenameSquadAsync(esiFleetId, created.Value, squad.Name, bossCharacterId, cancellationToken);
                if (!renamed.IsSuccess)
                    warnings.Add(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed,
                        $"Squad '{squad.Name}' was created in-game but could not be renamed.", "Fleet"));
                inGameWing.SquadNames.Add(squad.Name);
            }
        }

        await BroadcastRosterChangedAsync(fleetId, cancellationToken);
        return Result.Success(warnings.ToArray());
    }

    /// <summary>
    /// Renames a wing in the live fleet: finds the in-game wing by its OLD name (the rename hasn't been
    /// applied in-game yet) and renames it to <paramref name="newName"/>. A targeted push at the moment of the rename —
    /// PUSH STRUCTURE matches by name and would create a duplicate instead. Idempotent: a success no-op when no in-game
    /// wing carries the old name (already renamed, or the structure was never pushed). Mirrors only; no broadcast.
    /// </summary>
    public async Task<Result> RenameWingAsync(long esiFleetId, int bossCharacterId, string oldName, string newName,
        CancellationToken cancellationToken = default)
    {
        var live = await fleetClient.GetWingsAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!live.IsSuccess || live.Value is null)
            return live.ToResult("Fleet");

        if (live.Value.FirstOrDefault(wing => string.Equals(wing.Name, oldName, StringComparison.Ordinal)) is not { } target)
            return Result.Success(); // not in the live fleet (already renamed / structure not pushed) → nothing to do

        var renamed = await fleetClient.RenameWingAsync(esiFleetId, target.Id, newName, bossCharacterId, cancellationToken);
        return renamed.IsSuccess ? Result.Success() : renamed.ToResult("Fleet");
    }

    /// <summary>
    /// Renames a squad in the live fleet: finds the in-game squad by its OLD name within its wing (squad
    /// names aren't unique across wings, so the wing scopes the match) and renames it. Idempotent no-op when not present.
    /// Mirrors only; no broadcast.
    /// </summary>
    public async Task<Result> RenameSquadAsync(long esiFleetId, int bossCharacterId, string wingName, string oldSquadName, string newName,
        CancellationToken cancellationToken = default)
    {
        var live = await fleetClient.GetWingsAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!live.IsSuccess || live.Value is null)
            return live.ToResult("Fleet");

        var squad = live.Value
            .Where(wing => string.Equals(wing.Name, wingName, StringComparison.Ordinal))
            .SelectMany(wing => wing.Squads)
            .FirstOrDefault(squad => string.Equals(squad.Name, oldSquadName, StringComparison.Ordinal));
        if (squad is null)
            return Result.Success(); // not in the live fleet → nothing to do

        var renamed = await fleetClient.RenameSquadAsync(esiFleetId, squad.Id, newName, bossCharacterId, cancellationToken);
        return renamed.IsSuccess ? Result.Success() : renamed.ToResult("Fleet");
    }

    /// <summary>
    /// Reconciles the live fleet down to our plan by removing in-game wings/squads that are no longer in it (the
    /// destructive half of PUSH STRUCTURE). Guards: only EMPTY units are removed (no members;
    /// a wing also needs no kept squads), and the EVE defaults literally named "Wing 1"/"Squad 1" are never touched.
    /// Squads are removed before wings (a wing must be empty to delete). <paramref name="dryRun"/> returns the labels it
    /// WOULD remove without touching ESI, so the caller can confirm first; a real run returns what it removed and carries
    /// a warning per unit ESI refused. Self-healing + idempotent: a second run finds nothing left to remove.
    /// </summary>
    public async Task<Result<IReadOnlyList<string>>> DeleteObsoleteInGameUnitsAsync(
        IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId, bool dryRun, CancellationToken cancellationToken = default)
    {
        const string defaultWingName = "Wing 1";
        const string defaultSquadName = "Squad 1";

        var live = await fleetClient.GetWingsAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!live.IsSuccess || live.Value is null)
            return Result<IReadOnlyList<string>>.Failure([.. live.ToResult("Fleet").Messages]);

        var members = await fleetClient.GetMembersAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!members.IsSuccess || members.Value is null)
            return Result<IReadOnlyList<string>>.Failure([.. members.ToResult("Fleet").Messages]);
        var occupiedWingIds = members.Value.Select(member => member.WingId).ToHashSet();
        var occupiedSquadIds = members.Value.Select(member => member.SquadId).ToHashSet();

        // Our plan keyed by name (the reconcile key): which wing names exist, and which squad names per wing.
        var planSquadsByWing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var wing in await fleets.ListWingsAsync(fleetId))
            planSquadsByWing[wing.Name] = [.. (await fleets.ListSquadsAsync(wing.Id)).Select(squad => squad.Name)];

        var removed = new List<string>();
        var warnings = new List<ResultMessage>();

        // Squads first — a wing can only be deleted once it holds no squads.
        foreach (var wing in live.Value)
        {
            var planSquads = planSquadsByWing.GetValueOrDefault(wing.Name);
            foreach (var squad in wing.Squads)
            {
                if ((planSquads?.Contains(squad.Name) ?? false) || squad.Name == defaultSquadName || occupiedSquadIds.Contains(squad.Id))
                    continue; // keep: in the plan, an EVE default, or occupied
                var label = $"squad '{wing.Name} / {squad.Name}'";
                if (dryRun) { removed.Add(label); continue; }
                var deleted = await fleetClient.DeleteSquadAsync(esiFleetId, squad.Id, bossCharacterId, cancellationToken);
                if (deleted.IsSuccess)
                    removed.Add(label);
                else
                    warnings.Add(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed, $"Could not remove {label} in-game.", "Fleet"));
            }
        }

        // Then wings not in the plan, but only once empty: a default-named or occupied squad keeps the wing alive.
        foreach (var wing in live.Value)
        {
            if (planSquadsByWing.ContainsKey(wing.Name) || wing.Name == defaultWingName || occupiedWingIds.Contains(wing.Id))
                continue; // keep: in the plan, an EVE default, or occupied
            if (wing.Squads.Any(squad => squad.Name == defaultSquadName || occupiedSquadIds.Contains(squad.Id)))
                continue; // a protected/occupied squad keeps the wing from being emptied
            var label = $"wing '{wing.Name}'";
            if (dryRun) { removed.Add(label); continue; }
            var deleted = await fleetClient.DeleteWingAsync(esiFleetId, wing.Id, bossCharacterId, cancellationToken);
            if (deleted.IsSuccess)
                removed.Add(label);
            else
                warnings.Add(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed, $"Could not remove {label} in-game.", "Fleet"));
        }

        if (!dryRun && removed.Count > 0)
            await BroadcastRosterChangedAsync(fleetId, cancellationToken);
        return Result<IReadOnlyList<string>>.Success(removed, warnings.ToArray());
    }

    /// <summary>
    /// Invites the planned roster to the live fleet. Reads the roster + structure through
    /// <paramref name="fleets"/> (client-only or server fleet) and maps each member's planned wing/squad onto the live
    /// in-game wings/squads by name, so a pilot is invited straight into position. Pilots already in the live fleet and
    /// external members are skipped; invites run sequentially (rate-respecting); a rejected/CSPA-blocked pilot is recorded
    /// Failed with the ESI reason while the rest continue. Acceptance is observed later via the roster sync, not here.
    /// </summary>
    public async Task<Result<IReadOnlyList<EsiInviteOutcome>>> InviteRosterAsync(IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId,
        CancellationToken cancellationToken = default)
    {
        // Skip pilots already in the live fleet; best-effort — if the boss read fails, invite everyone planned.
        var live = await fleetClient.GetMembersAsync(esiFleetId, bossCharacterId, cancellationToken);
        HashSet<int> present = live is { IsSuccess: true, Value: { } liveMembers }
            ? liveMembers.Select(member => member.CharacterId).ToHashSet()
            : [];

        var resolver = await _BuildEsiPositionResolverAsync(fleets, fleetId, esiFleetId, bossCharacterId, cancellationToken);
        if (!resolver.IsSuccess)
            return Result<IReadOnlyList<EsiInviteOutcome>>.Failure([.. resolver.Messages]);

        var outcomes = new List<EsiInviteOutcome>();
        foreach (var member in await fleets.ListMembersAsync(fleetId))
        {
            if (member.IsExternal || present.Contains(member.CharacterId))
                continue; // external members have no client to accept; present pilots are already in

            if (resolver.Value!.Resolve(member.Role, member.WingId, member.SquadId) is not { } position)
            {
                outcomes.Add(new EsiInviteOutcome(member.CharacterId, EsiInviteStatus.Failed,
                    "This pilot's wing/squad isn't in the in-game fleet yet — push the structure first."));
                continue;
            }

            var invite = await fleetClient.InviteMemberAsync(
                esiFleetId, member.CharacterId, position.Role, position.WingId, position.SquadId, bossCharacterId, cancellationToken);
            outcomes.Add(invite.IsSuccess
                ? new EsiInviteOutcome(member.CharacterId, EsiInviteStatus.Invited, null)
                : new EsiInviteOutcome(member.CharacterId, EsiInviteStatus.Failed, DescribeInviteFailure(invite.Error)));
        }

        return Result<IReadOnlyList<EsiInviteOutcome>>.Success(outcomes);
    }

    /// <summary>
    /// Invites a single planned member into the live fleet at their planned position (Auto Invite). Idempotent: a
    /// pilot already in the live fleet is a no-op success. Resolves the member's internal wing/squad onto the live in-game
    /// wings/squads by name (the same mapping as <see cref="InviteRosterAsync"/>); when that wing/squad isn't pushed yet the
    /// invite is skipped with a reason, and an offline pilot yields the 420 reason via <see cref="DescribeInviteFailure"/>.
    /// The auto path stays silent on failure (no toast spam) — the manual INVITE ROSTER surfaces per-pilot outcomes.
    /// </summary>
    public async Task<Result> InviteMemberAsync(IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId,
        int characterId, FleetRole role, long wingId, long squadId, CancellationToken cancellationToken = default)
    {
        var live = await fleetClient.GetMembersAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (live is { IsSuccess: true, Value: { } liveMembers } && liveMembers.Any(member => member.CharacterId == characterId))
            return Result.Success(); // already in the live fleet — nothing to invite

        var resolver = await _BuildEsiPositionResolverAsync(fleets, fleetId, esiFleetId, bossCharacterId, cancellationToken);
        if (!resolver.IsSuccess)
            return Result.Failure([.. resolver.Messages]);

        if (resolver.Value!.Resolve(role, wingId, squadId) is not { } position)
            return Result.Failure(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed,
                "This pilot's wing/squad isn't in the in-game fleet yet — push the structure first.", "Fleet"));

        var invite = await fleetClient.InviteMemberAsync(
            esiFleetId, characterId, position.Role, position.WingId, position.SquadId, bossCharacterId, cancellationToken);
        return invite.IsSuccess
            ? Result.Success()
            : Result.Failure(new ResultMessage(MessageSeverity.Warning, MessageCodes.EsiFailed,
                DescribeInviteFailure(invite.Error) ?? "The in-game invite failed.", "Fleet"));
    }

    /// <summary>
    /// Reflects a member's assigned structure position in the live fleet, picking the right ESI action by first reading
    /// who is actually in the in-game fleet (the boss roster read, ~5s-cached by the ESI pipeline — within the boss
    /// mirror's poll window it costs no extra error budget). A pilot already in the fleet is MOVED to the position
    /// a pilot not yet in it is INVITED to the position when <paramref name="invite"/> is on;
    /// otherwise nothing happens. This is the smart guard against the doomed "move a non-member" call — a guaranteed ESI
    /// 400 that also burns error budget. A real move failure (present pilot) surfaces to the caller; the invite is
    /// best-effort/silent. When the presence read itself fails (transient), it skips rather than guessing — the 5s mirror
    /// and the next action reconcile.
    /// </summary>
    public async Task<Result> SyncMemberPositionAsync(IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId,
        int characterId, FleetRole role, long wingId, long squadId, bool invite, CancellationToken cancellationToken = default)
    {
        var live = await fleetClient.GetMembersAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!live.IsSuccess || live.Value is null)
            return Result.Success(); // can't confirm presence this cycle — skip rather than fire a blind action

        if (live.Value.Any(member => member.CharacterId == characterId))
            return await MoveMemberAsync(fleets, fleetId, esiFleetId, bossCharacterId, characterId, role, wingId, squadId, cancellationToken);

        if (invite)
            // Already confirmed absent above; InviteMemberAsync re-checks (a cache hit) and resolves the position by name.
            await InviteMemberAsync(fleets, fleetId, esiFleetId, bossCharacterId, characterId, role, wingId, squadId, cancellationToken);

        return Result.Success(); // not in the fleet (invited or left as-is) — never a move on a non-member
    }

    // Reads the live in-game wing/squad ids (by name) and our internal structure (by id) into one resolver that
    // translates a member's internal role + wing/squad onto its live ESI position. Shared by move and invite:
    // both need the same name-based mapping, and reading it live keeps the push migration-free + self-healing. Fails when
    // the live boss read fails (the caller surfaces it); an empty live fleet just resolves no positions ("push first").
    private async Task<Result<EsiPositionResolver>> _BuildEsiPositionResolverAsync(
        IFleetClient fleets, long fleetId, long esiFleetId, int bossCharacterId, CancellationToken cancellationToken)
    {
        var liveWings = await fleetClient.GetWingsAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!liveWings.IsSuccess || liveWings.Value is null)
            return Result<EsiPositionResolver>.Failure([.. liveWings.ToResult("Fleet").Messages]);

        var esiWingByName = new Dictionary<string, long>(StringComparer.Ordinal);
        var esiSquadByName = new Dictionary<(string Wing, string Squad), long>();
        foreach (var wing in liveWings.Value)
        {
            esiWingByName[wing.Name] = wing.Id;
            foreach (var squad in wing.Squads)
                esiSquadByName[(wing.Name, squad.Name)] = squad.Id;
        }

        var wingNameById = new Dictionary<long, string>();
        var squadById = new Dictionary<long, (string WingName, string SquadName)>();
        foreach (var wing in await fleets.ListWingsAsync(fleetId))
        {
            wingNameById[wing.Id] = wing.Name;
            foreach (var squad in await fleets.ListSquadsAsync(wing.Id))
                squadById[squad.Id] = (wing.Name, squad.Name);
        }

        return Result<EsiPositionResolver>.Success(new EsiPositionResolver(wingNameById, squadById, esiWingByName, esiSquadByName));
    }

    // POST /fleets/{id}/members/ returns 420 (without the error-limit headers) when the target pilot is offline
    // rather than the error limiter (esi-issues #314). Surface that reason instead of the raw rate-limit status text.
    private static string? DescribeInviteFailure(EsiError? error) => error switch
    {
        { HttpStatus: 420, RetryAfter: null } => "Pilot must be online in EVE to receive a fleet invite.",
        { HttpStatus: 420 } => "ESI error limit reached — wait a moment and retry the invite.",
        _ => error?.Message
    };

    private async Task BroadcastRosterChangedAsync(long fleetId, CancellationToken cancellationToken) =>
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleetId, FleetChangeKind.RosterChanged)),
            EventTarget.Local, cancellationToken);

    // The literal ESI role strings (FleetRole's EnumMember values); null = no in-game equivalent (Unassigned).
    private static string? ToEsiRole(FleetRole role) => role switch
    {
        FleetRole.FleetCommander => "fleet_commander",
        FleetRole.WingCommander => "wing_commander",
        FleetRole.SquadCommander => "squad_commander",
        FleetRole.SquadMember => "squad_member",
        _ => null
    };

    // Maps a member's internal role + wing/squad onto the live in-game ESI position by name; null = no in-game spot
    // (unassigned role, or the target wing/squad isn't in the live fleet yet → "push structure first").
    private sealed record EsiPositionResolver(
        IReadOnlyDictionary<long, string> WingNameById,
        IReadOnlyDictionary<long, (string WingName, string SquadName)> SquadById,
        IReadOnlyDictionary<string, long> EsiWingByName,
        IReadOnlyDictionary<(string, string), long> EsiSquadByName)
    {
        public (string Role, long? WingId, long? SquadId)? Resolve(FleetRole role, long internalWingId, long internalSquadId)
        {
            if (ToEsiRole(role) is not { } esiRole)
                return null;
            if (role == FleetRole.FleetCommander)
                return (esiRole, null, null);
            if (!WingNameById.TryGetValue(internalWingId, out var wingName) || !EsiWingByName.TryGetValue(wingName, out var esiWingId))
                return null;
            if (role == FleetRole.WingCommander)
                return (esiRole, esiWingId, null);
            if (!SquadById.TryGetValue(internalSquadId, out var squad) || !EsiSquadByName.TryGetValue(squad, out var esiSquadId))
                return null;
            return (esiRole, esiWingId, esiSquadId);
        }
    }
}
