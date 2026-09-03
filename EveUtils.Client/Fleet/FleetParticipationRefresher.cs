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
    /// What each (server, character) last actually answered with. A server being briefly unreachable is not the same
    /// news as a pilot having left it: replacing the whole set on a blip would stop a commander publishing and take
    /// the fleet id off their run window — the very failure this class exists to prevent. The fleets listing has
    /// always kept an unreachable server's rows for exactly this reason, and dropping that on the way here would
    /// have traded one silent commander for another.
    /// </summary>
    private readonly Dictionary<(string Server, int CharacterId), FleetParticipant[]> _lastAnswered = [];
    private readonly Lock _gate = new();

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
            // fleets listing gives each server's load. Without the session list there is no character to ask about,
            // so this server contributes everything it last said.
            try { loaded = await sessions.LoadAllAsync(server, cancellationToken); }
            catch { participants.AddRange(_LastAnsweredBy(server)); continue; }

            foreach (ClientSessionTokens session in loaded)
            {
                IReadOnlyList<FleetInfo> fleets;
                try { fleets = await transport.ListMyFleetsAsync(server, session.CharacterId, cancellationToken); }
                catch { participants.AddRange(_LastAnsweredBy(server)); continue; }

                // Signing up in advance to a Forming fleet is membership without broadcast: you only share once the
                // FC has actually started it.
                List<FleetParticipant> answered = [];
                foreach (FleetInfo fleet in fleets
                             .Where(fleet => fleet.State == FleetState.Active && fleet.Activation == FleetActivation.Active))
                    answered.Add(new FleetParticipant(session.CharacterId, fleet.Id, ClientOnly: false,
                        await _CommanderOfAsync(server, fleet.Id, session.CharacterId, cancellationToken)));

                _Remember(server, session.CharacterId, [.. answered]);
                participants.AddRange(answered);
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

            IReadOnlyList<FleetMember> roster = await repository.ListMembersAsync(fleet.Id, cancellationToken);

            // Only MY characters: a client-only fleet lists its externals too (ET-46), and this client can no more
            // publish for someone else's pilot than it can read their game log. With none of mine on the roster the
            // owner still flies it, and a fleet they created is theirs to command.
            var members = roster
                .Select(member => member.CharacterId)
                .Where(mine.Contains)
                .Distinct()
                .ToList();
            // A local roster is always readable, so this is never the "could not say" null that a server fleet can
            // produce — it falls back to the creator, who is the one CreateFleetCommand seats as FC.
            int commander = roster
                .FirstOrDefault(member => member.Role == FleetRole.FleetCommander)?.CharacterId ?? ownerId;

            participants.AddRange((members.Count > 0 ? members : [ownerId])
                .Select(characterId => new FleetParticipant(characterId, fleet.Id, ClientOnly: true, commander)));
        }
    }

    private void _Remember(string server, int characterId, FleetParticipant[] answered)
    {
        lock (_gate)
            _lastAnswered[(server, characterId)] = answered;
    }

    /// <summary>What one character on this server last answered with — used in place of the silence a failed read
    /// would otherwise contribute.</summary>
    private FleetParticipant[] _LastAnsweredBy(string server, int characterId)
    {
        lock (_gate)
            return _lastAnswered.GetValueOrDefault((server, characterId), []);
    }

    /// <summary>The same, for every character on a server whose session list could not even be read.</summary>
    private FleetParticipant[] _LastAnsweredBy(string server)
    {
        lock (_gate)
            return [.. _lastAnswered.Where(entry => entry.Key.Server == server).SelectMany(entry => entry.Value)];
    }

    /// <summary>
    /// Who commands this fleet according to the ET roster, or null when the roster could not be read. Null is the
    /// "cannot say" a shared run turns into hidden controls with a reason on screen, rather than a guess either way.
    /// </summary>
    private async Task<int?> _CommanderOfAsync(
        string server, long fleetId, int characterId, CancellationToken cancellationToken)
    {
        try
        {
            return (await transport.ListMembersAsync(server, fleetId, characterId, cancellationToken))
                .FirstOrDefault(member => member.Role == FleetRole.FleetCommander)?.CharacterId;
        }
        catch
        {
            return null;
        }
    }
}
