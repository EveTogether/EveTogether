using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>One character on a fleet's roster, as far as the attendance list needs it (ET-230).</summary>
/// <param name="LastSeenAt">When the server last heard this character's client — what tells a pilot who left from one
/// never heard from (ET-70).</param>
public sealed record RosterCharacter(long CharacterId, bool IsExternal, DateTimeOffset? LastSeenAt);

/// <summary>
/// The fleet roster and the live presence beside it, for both HOMEFRONT sections (ET-230). The roster is read the way
/// the rest of this client reads it for a fleet it is in — the server's for a server fleet, the local store for a
/// client-only one — and only for a fleet this client takes part in right now. Anything else has no roster to show,
/// and no presence either: presence is the live fleet view and is never stored (Jithran, 2026-09-11).
/// </summary>
public static class AttendanceRoster
{
    /// <summary>The roster of <paramref name="fleetId"/>, or null when this client is not in that fleet right now or
    /// the roster could not be read.</summary>
    public static async Task<IReadOnlyList<RosterCharacter>?> ReadAsync(IServiceProvider services, long fleetId)
    {
        if (services.GetService<IFleetParticipation>()?.Current.FirstOrDefault(participant => participant.FleetId == fleetId)
            is not { FleetId: > 0 } participant)
            return null;

        try
        {
            if (participant.ClientOnly)
            {
                using IServiceScope scope = services.CreateScope();
                return [.. (await scope.ServiceProvider.GetRequiredService<IFleetRepository>().ListMembersAsync(fleetId))
                    .Select(member => new RosterCharacter(member.CharacterId, member.IsExternal, member.LastSeenAt))];
            }

            if (participant.ServerAddress is not { } server || services.GetService<IFleetTransportClient>() is not { } transport)
                return null;

            return [.. (await transport.ListMembersAsync(server, fleetId, participant.CharacterId))
                .Select(member => new RosterCharacter(member.CharacterId, member.IsExternal, member.LastSeenAt))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A server that went quiet leaves the list without a roster for now, the same "cannot say" the run controls
            // treat it as; the next read tries again.
            return null;
        }
    }

    /// <summary>The same presence the fleet screens show (<see cref="FleetMemberPresence.Read"/>), in a word — and
    /// null when there is nothing to say, which the list then shows as nothing at all.</summary>
    /// <param name="reported">What the character's own client last said about their game, if it said anything.</param>
    /// <param name="lastHeardAt">When that client was last heard, by this window or by the server.</param>
    public static FleetMemberPresenceState? PresenceOf(IServiceProvider services, long characterId, bool isLocal,
        PresenceState? reported, DateTimeOffset? lastHeardAt, DateTimeOffset now)
    {
        if (isLocal)
            return services.GetService<ILocalCharacterPresence>()?.IsInGame(checked((int)characterId)) is { } inGame
                ? FleetMemberPresence.Read(inGame, PresenceState.Unknown, isSilent: false)
                : null;

        return FleetMemberPresence.Read(null, reported ?? PresenceState.Unknown, FleetMemberPresence.IsSilent(lastHeardAt, now));
    }

    /// <summary>A character's name: this client's own registry first, then public ESI — null when neither knows it.</summary>
    public static async Task<string?> NameOfAsync(IServiceProvider services, long characterId)
    {
        if (services.GetService<ICharacterRegistry>() is { } registry
            && (await registry.GetAllAsync()).FirstOrDefault(character => character.EsiCharacterId == characterId) is { } local)
            return local.Name;

        if (services.GetService<IExternalCharacterLookup>() is not { } lookup)
            return null;

        ExternalCharacterInfo info = await lookup.LookupAsync(checked((int)characterId));
        return info.Exists ? info.Name : null;
    }

    /// <summary>The ids of every character registered on this client.</summary>
    public static async Task<IReadOnlySet<long>> OwnCharacterIdsAsync(IServiceProvider services) =>
        services.GetService<ICharacterRegistry>() is { } registry
            ? (await registry.GetAllAsync()).Select(character => character.EsiCharacterId).OfType<int>()
                .Select(characterId => (long)characterId).ToHashSet()
            : new HashSet<long>();
}
