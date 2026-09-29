using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Who is in each linked fleet's in-game fleet right now, as the boss's ESI poll last read it (ET-394) — pilots who do
/// not use EVE Together included, which the fleet roster never holds. It is what lets the map count and frame those
/// pilots as members of the fleet. In memory only, and only on the boss's client: nobody else can read the in-game fleet.
/// </summary>
public sealed class InGameFleetRosters(TimeProvider clock) : ISingletonService
{
    /// <summary>The poll runs every 5 s; a roster a minute old means the poll stopped, and is no longer vouched for.</summary>
    public static readonly TimeSpan TrustedFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Snapshot> _rosters = new();

    /// <summary>The key a fleet is known by: fleet ids are only unique per server, and a client-only fleet has none.</summary>
    public static string KeyOf(string? serverAddress, long fleetId) => $"{serverAddress ?? "local"}:{fleetId}";

    /// <summary>The in-game members of the fleet, or none when its roster was never read or is older than <see cref="TrustedFor"/>.</summary>
    public IReadOnlyCollection<int> MembersOf(string? serverAddress, long fleetId) =>
        _rosters.TryGetValue(KeyOf(serverAddress, fleetId), out Snapshot? snapshot) && clock.GetUtcNow() - snapshot.ReadAt <= TrustedFor
            ? snapshot.CharacterIds
            : [];

    internal void Record(string key, IEnumerable<int> characterIds) =>
        _rosters[key] = new Snapshot([.. characterIds.Distinct()], clock.GetUtcNow());

    internal void Forget(string key) => _rosters.TryRemove(key, out _);

    private sealed record Snapshot(IReadOnlyCollection<int> CharacterIds, DateTimeOffset ReadAt);
}
