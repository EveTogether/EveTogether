using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>
/// Who commands the in-game fleet a character is in right now — the fact <c>RunControlAuthority</c> is decided
/// against, and the one thing ET-105 was built without a source for.
///
/// It rides the endpoint the two ESI fleet services already use, <c>GET /characters/{id}/fleet/</c>: the only
/// per-member fleet read, any member may make it, and it carries <c>fleet_boss_id</c> outright. So there is no poll
/// loop here — a caller asks on its own tick, and the answer is refreshed no faster than that endpoint's own 60s ESI
/// cache, which is also the ceiling on how fresh "who is the boss" can honestly be.
///
/// <see cref="EsiFleetSyncService"/> cannot answer this: it reads the roster, which is boss-only, so a member never
/// learns anything from it. Nor can the stored <c>Fleet.EsiFleetBossId</c>: that is written once when the fleet is
/// coupled and never rewritten on a handover, which is precisely the "captured at start" the ruling forbids.
///
/// A read that fails leaves <see cref="BossOf"/> null. Not knowing stays not knowing — never a stale name.
/// </summary>
public sealed class FleetBossTracker(IEsiFleetClient fleetClient) : ISingletonService
{
    /// <summary>The endpoint's own ESI cache TTL. Asking faster cannot return a newer answer, it only spends the
    /// error-limit budget.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<int, Reading> _readings = new();

    /// <summary>The boss ESI last reported for this character's fleet, or null when it has not said: no read has been
    /// made yet, the read failed, or the character is in no fleet at all.</summary>
    public int? BossOf(int characterId) =>
        _readings.TryGetValue(characterId, out Reading? reading) ? reading.BossCharacterId : null;

    /// <summary>
    /// Read the boss again if the last answer has gone stale; otherwise do nothing. Safe to call on a 1 Hz tick, and
    /// safe to call from two windows at once — the slot is stamped before the call goes out, so a second caller finds
    /// it fresh instead of sending its own.
    /// </summary>
    public async Task RefreshAsync(int characterId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        _readings.TryGetValue(characterId, out Reading? previous);
        if (previous is not null && nowUtc - previous.ReadAtUtc < Ttl)
            return;

        // The previous answer stays readable while this read is in flight, so the controls do not blink away every
        // time the cache expires.
        _readings[characterId] = new Reading(previous?.BossCharacterId, nowUtc);

        EsiResult<EsiCharacterFleet> result = await fleetClient.GetCharacterFleetAsync(characterId, cancellationToken);
        _readings[characterId] = new Reading(result.IsSuccess ? result.Value?.FleetBossId : null, nowUtc);
    }

    private sealed record Reading(int? BossCharacterId, DateTime ReadAtUtc);
}
