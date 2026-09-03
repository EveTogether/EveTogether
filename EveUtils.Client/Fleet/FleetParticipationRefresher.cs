using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Rebuilds <see cref="IFleetParticipation"/> from what this client is actually a member of, without any window
/// having to be open. The set used to be written only by the fleets view model, so a pilot who never opened that
/// window published nothing and their run window knew no fleet (ET-152).
/// </summary>
public sealed class FleetParticipationRefresher(
    IClientSessionStore sessions,
    IFleetTransportClient transport,
    IFleetParticipation participation,
    ICharacterRegistry characters,
    IServiceScopeFactory scopeFactory) : ISingletonService
{
    /// <summary>
    /// Sweeps every coupled server and the local repository and replaces the participation set. This is the only
    /// writer, so the set never depends on which screen happened to sweep last.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        List<FleetParticipant> participants = [];
        await _AddServerFleetsAsync(participants, cancellationToken);
        await _AddClientOnlyFleetsAsync(participants, cancellationToken);
        participation.Set(participants);
    }

    /// <summary>
    /// One entry per (character, fleet) this client holds a session for. A character listing a fleet is what
    /// membership means here, so multi-boxing several toons into one fleet feeds every one of their graphs.
    /// </summary>
    private async Task _AddServerFleetsAsync(List<FleetParticipant> participants, CancellationToken cancellationToken)
    {
        foreach (string server in await sessions.ListServersAsync(cancellationToken))
        {
            IReadOnlyList<ClientSessionTokens> loaded;
            // One unreachable or stale server may not cost the others their participation — the same isolation the
            // fleets listing gives each server's load.
            try { loaded = await sessions.LoadAllAsync(server, cancellationToken); }
            catch { continue; }

            foreach (ClientSessionTokens session in loaded)
            {
                IReadOnlyList<FleetInfo> fleets;
                try { fleets = await transport.ListMyFleetsAsync(server, session.CharacterId, cancellationToken); }
                catch { continue; }

                // Signing up in advance to a Forming fleet is membership without broadcast: you only share once the
                // FC has actually started it.
                participants.AddRange(fleets
                    .Where(fleet => fleet.State == FleetState.Active && fleet.Activation == FleetActivation.Active)
                    .Select(fleet => new FleetParticipant(session.CharacterId, fleet.Id, ClientOnly: false)));
            }
        }
    }

    /// <summary>
    /// A client-only fleet lives purely in this client, so it is read from the local repository and always
    /// participates — its samples feed local graphs and never leave the machine.
    /// </summary>
    private async Task _AddClientOnlyFleetsAsync(List<FleetParticipant> participants, CancellationToken cancellationToken)
    {
        var mine = (await characters.GetAllAsync(cancellationToken))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .ToHashSet();
        if (mine.Count == 0)
            return;

        using IServiceScope scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();

        foreach (int ownerId in mine)
        foreach (FleetEntity fleet in await repository.ListByCreatorAsync(ownerId, cancellationToken))
        {
            if (!fleet.IsClientOnly || fleet.State != FleetState.Active || fleet.Activation == FleetActivation.Concluded)
                continue;

            // Only MY characters: a client-only fleet lists its externals too (ET-46), and this client can no more
            // publish for someone else's pilot than it can read their game log. With none of mine on the roster the
            // owner still flies it.
            var members = (await repository.ListMembersAsync(fleet.Id, cancellationToken))
                .Select(member => member.CharacterId)
                .Where(mine.Contains)
                .Distinct()
                .ToList();

            participants.AddRange((members.Count > 0 ? members : [ownerId])
                .Select(characterId => new FleetParticipant(characterId, fleet.Id, ClientOnly: true)));
        }
    }
}
