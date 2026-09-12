using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Runs;

/// <summary>
/// Who the run's own multi-pick (ET-210) ticks by default, and what it remembers across a restart (ET-270).
///
/// Jithran's own priority, set after measuring the first pass against how he actually multiboxes: "if I'm in a
/// fleet I assume my fleet mates are riding along; unticking one is the exception, not the rule." So the default is
/// fleet-first, not memory-first:
///
/// <list type="number">
/// <item>The pilot is in an active fleet right now: every one of <i>this client's own</i> characters sharing that
/// fleet (<see cref="FleetCharacterIdsFor"/>) is ticked, minus whichever ones were unticked the last time this same
/// fleet started a site (<see cref="FleetExcludedKeyFor"/>) — the one thing this remembers per fleet rather than
/// per pilot.</item>
/// <item>The pilot is not in a fleet right now (or not in one at all): fall back to the original ET-270 scope —
/// the last full pick this same character was ever part of (<see cref="KeyFor"/>), filtered to who is available.</item>
/// </list>
///
/// Either way, a character who is logged out right now never comes back ticked on its own — the pilot ticks it
/// back in by hand if that is still what they want (ET-270 AC-2).
///
/// One mechanism, two readers: <c>ClipboardSignatureOffer</c>/<c>ClipboardMissionOffer</c> (before a run window
/// exists at all) and <c>ManualRunStartViewModel</c> (Tools -> Start run, ET-255/ET-221's own preselection). Each
/// keeps its own already-established way to reach the settings store — <see cref="ISettingRepository"/> for the
/// clipboard offers, the CQRS <c>GetSettingsQuery</c>/<c>SetSettingCommand</c> pair for the manual dialog — so only
/// the key formats, the parsing and the priority rule above live here.
/// </summary>
public static class OwnCharacterPickMemory
{
    private const string LastPickKeyPrefix = "ui.own-toons.last-picked.";
    private const string FleetExcludedKeyPrefix = "ui.own-toons.fleet-excluded.";

    /// <summary>The original ET-270 scope: this character's own last full pick, used only when the pilot is not in
    /// a fleet right now. Filed under every picked character's own id, not only the one who ends up "pilot"
    /// (<c>picked[0]</c>, decided by the candidate list's own fixed order, not by this memory — ET-221 measured
    /// that already) — so whichever one of them next triggers a start still finds the same team.</summary>
    public static string KeyFor(int characterId) => $"{LastPickKeyPrefix}{characterId}";

    /// <summary>Who this fleet's own multi-pick excludes by default — the exception Jithran expects to type
    /// occasionally, not the rule. One row per fleet, not per character: the whole point of the fleet-first default
    /// is that it does not care which of the fleet's own characters happened to trigger this start.</summary>
    public static string FleetExcludedKeyFor(long fleetId) => $"{FleetExcludedKeyPrefix}{fleetId}";

    /// <summary>Parses a stored value back into character ids, oldest-to-newest pick order preserved. Anything
    /// that does not parse as a positive id is dropped rather than failing the whole read — a value from an older
    /// build or a hand-edited database should read back as "remembers less", never as "remembers nothing" or a
    /// thrown exception.</summary>
    public static IReadOnlyList<int> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, out int id) ? id : (int?)null)
                .Where(id => id is > 0)
                .Select(id => id!.Value)];

    public static string Serialize(IReadOnlyList<int> characterIds) => string.Join(',', characterIds);

    /// <summary>What the no-fleet fallback is allowed to auto-tick (ET-270 AC-2): a character logged out since the
    /// last pick never comes back silently ticked. <paramref name="flyingCharacterIds"/> is this client's own "EVE
    /// client running" set — the same one the multi-select's own hint text already reads off (ET-210/ET-216).</summary>
    public static IReadOnlyList<int> FilterAvailable(IReadOnlyList<int> rememberedIds,
        IReadOnlyCollection<int> flyingCharacterIds) =>
        [.. rememberedIds.Where(flyingCharacterIds.Contains)];

    /// <summary>The anchor's own active fleet, as the set of this client's own character ids sharing it (including
    /// the anchor) — null when the anchor is not in any fleet right now, which is the signal that switches
    /// <see cref="ResolvePreselection"/> over to the no-fleet fallback. <see cref="IFleetParticipation.Current"/>
    /// only ever lists this client's own registered characters (<c>FleetParticipationRefresher</c> filters every
    /// server and client-only fleet down to "mine" before publishing it), so this is never someone else's roster.</summary>
    public static IReadOnlyCollection<int>? FleetCharacterIdsFor(int? anchorCharacterId,
        IReadOnlyList<FleetParticipant> participation)
    {
        long? fleetId = FleetIdFor(anchorCharacterId, participation);
        return fleetId is { } fid
            ? [.. participation.Where(participant => participant.FleetId == fid).Select(participant => participant.CharacterId)]
            : null;
    }

    /// <summary>The anchor's own active fleet id, or null when the anchor is not in one right now (or has no
    /// anchor at all) — what the resolve/save pair above keys the per-fleet exclusion row on.</summary>
    public static long? FleetIdFor(int? anchorCharacterId, IReadOnlyList<FleetParticipant> participation)
    {
        if (anchorCharacterId is not { } anchor)
            return null;

        foreach (FleetParticipant participant in participation)
            if (participant.CharacterId == anchor)
                return participant.FleetId;

        return null;
    }

    /// <summary>The one priority rule (see the class summary): fleet-first, remembered-pick fallback, logged-out
    /// characters never ticked. Pure — every caller loads whatever a fleet or a memory row it needs through its own
    /// settings access, then hands the result here.</summary>
    public static IReadOnlyList<int> ResolvePreselection(int anchorCharacterId,
        IReadOnlyCollection<int> flyingCharacterIds, IReadOnlyCollection<int>? fleetOwnCharacterIds,
        IReadOnlyList<int> fleetExcludedCharacterIds, IReadOnlyList<int> rememberedCharacterIds)
    {
        IReadOnlyList<int> ticked = fleetOwnCharacterIds is not null
            ? [.. fleetOwnCharacterIds.Where(flyingCharacterIds.Contains).Except(fleetExcludedCharacterIds)]
            : FilterAvailable(rememberedCharacterIds, flyingCharacterIds);

        return ticked.Contains(anchorCharacterId) ? ticked : [anchorCharacterId, .. ticked];
    }

    /// <summary>The clipboard offers' own read. No anchor means no lookup at all: there is nobody to key a fleet or
    /// a memory row on, same as before this ticket — the multi-select opens with nothing preselected.</summary>
    public static async Task<IReadOnlyList<int>?> ResolvePreselectionAsync(ISettingRepository? settings,
        int? anchorCharacterId, IReadOnlyCollection<int> flyingCharacterIds,
        IReadOnlyCollection<int>? fleetOwnCharacterIds, long? fleetId)
    {
        if (anchorCharacterId is not { } anchor)
            return null;

        IReadOnlyList<int> excluded = [];
        IReadOnlyList<int> remembered = [];
        if (settings is not null)
        {
            IReadOnlyList<ClientSetting> all = await settings.ListAsync();
            if (fleetId is { } fid)
                excluded = Parse(all.FirstOrDefault(setting => setting.Key == FleetExcludedKeyFor(fid))?.Value);
            else
                remembered = Parse(all.FirstOrDefault(setting => setting.Key == KeyFor(anchor))?.Value);
        }

        return ResolvePreselection(anchor, flyingCharacterIds, fleetOwnCharacterIds, excluded, remembered);
    }

    /// <summary>The clipboard offers' own write, fire-and-forget the same way every other settings write a plain
    /// property setter causes in this app is (<c>ManualRunStartViewModel</c>'s own comment on
    /// <c>OnSelectedActivityKindChanged</c> names the one exception, and this is not it). In a fleet, this also
    /// records who was unticked out of the fleet's own default (empty once everyone is ticked again, clearing a
    /// stale exclusion); either way, every picked character's own last-full-pick row is kept fresh as the fallback
    /// for whenever this same character next starts without a fleet.</summary>
    public static async Task SaveAsync(ISettingRepository settings, IReadOnlyList<int> pickedCharacterIds,
        IReadOnlyCollection<int>? fleetOwnCharacterIds, long? fleetId)
    {
        if (pickedCharacterIds.Count == 0)
            return;

        if (fleetId is { } fid && fleetOwnCharacterIds is not null)
        {
            IReadOnlyList<int> excluded = [.. fleetOwnCharacterIds.Except(pickedCharacterIds)];
            await settings.UpsertAsync(FleetExcludedKeyFor(fid), Serialize(excluded));
        }

        string value = Serialize(pickedCharacterIds);
        foreach (int id in pickedCharacterIds)
            await settings.UpsertAsync(KeyFor(id), value);
    }

    /// <summary>Same rule as the <see cref="ISettingRepository"/> overload above, for <c>ManualRunStartViewModel</c>,
    /// which already reaches every other setting of its own (<c>LastKindSettingKey</c>, ET-255) through
    /// <see cref="IDispatcher"/> rather than the repository directly — one behaviour, read through whichever access
    /// its own caller already has on hand.</summary>
    public static async Task<IReadOnlyList<int>?> ResolvePreselectionAsync(IDispatcher dispatcher,
        int? anchorCharacterId, IReadOnlyCollection<int> flyingCharacterIds,
        IReadOnlyCollection<int>? fleetOwnCharacterIds, long? fleetId)
    {
        if (anchorCharacterId is not { } anchor)
            return null;

        IReadOnlyList<SettingDto> all = await dispatcher.Query(new GetSettingsQuery());
        IReadOnlyList<int> excluded = fleetId is { } fid
            ? Parse(all.FirstOrDefault(setting => setting.Key == FleetExcludedKeyFor(fid))?.Value)
            : [];
        IReadOnlyList<int> remembered = fleetId is null
            ? Parse(all.FirstOrDefault(setting => setting.Key == KeyFor(anchor))?.Value)
            : [];

        return ResolvePreselection(anchor, flyingCharacterIds, fleetOwnCharacterIds, excluded, remembered);
    }

    public static async Task SaveAsync(IDispatcher dispatcher, IReadOnlyList<int> pickedCharacterIds,
        IReadOnlyCollection<int>? fleetOwnCharacterIds, long? fleetId)
    {
        if (pickedCharacterIds.Count == 0)
            return;

        if (fleetId is { } fid && fleetOwnCharacterIds is not null)
        {
            IReadOnlyList<int> excluded = [.. fleetOwnCharacterIds.Except(pickedCharacterIds)];
            await dispatcher.Send(new SetSettingCommand(FleetExcludedKeyFor(fid), Serialize(excluded)));
        }

        string value = Serialize(pickedCharacterIds);
        foreach (int id in pickedCharacterIds)
            await dispatcher.Send(new SetSettingCommand(KeyFor(id), value));
    }
}
