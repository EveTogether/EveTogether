using EveUtils.Server.Api.Dtos;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Api;

/// <summary>
/// Read-only bridge from the existing repositories to the public API DTOs — the server-side counterpart of the
/// client's <c>LocalApiQueries</c>. No second data layer: it reads what the rest of the server already reads and
/// maps it to shapes that carry nothing an external consumer should not see.
/// </summary>
public sealed class ServerApiQueries(
    IFleetRepository fleets, IFleetCompositionRepository compositions) : IScopedService
{
    /// <summary>
    /// The fleets this key may see. A key with no owner has admin scope over all server data (ratified decision 3)
    /// and gets the whole directory; a key issued to a character gets what that character could discover on the
    /// server anyway — the open fleets. Without this split every key would be an admin key, because the
    /// character-scoping the decision leaves incremental is not built yet.
    /// </summary>
    public async Task<IReadOnlyList<ApiFleetListItem>> GetFleetsAsync(
        int? ownerCharacterId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FleetEntity> all = ownerCharacterId is null
            ? await fleets.ListByStateAsync(FleetState.Active, cancellationToken)
            : await fleets.ListOpenAsync(cancellationToken);

        return [.. all.Select(fleet => new ApiFleetListItem(
            fleet.Id, fleet.Name, fleet.Description, fleet.CreatorCharacterId,
            fleet.State.ToString(), fleet.Activation.ToString(), fleet.Visibility.ToString(),
            fleet.FleetCompositionId))];
    }

    /// <summary>
    /// One fleet with its wings, squads and roster; null when it does not exist, and equally null when this key
    /// may not see it. A filter on the list that the detail route walks around is no filter at all, so the same
    /// split applies here — a key scoped to a character cannot fetch an invite-only fleet by guessing its id.
    /// </summary>
    public async Task<ApiFleetDetail?> GetFleetAsync(
        long fleetId, int? ownerCharacterId, CancellationToken cancellationToken = default)
    {
        FleetEntity? fleet = await fleets.GetAsync(fleetId, cancellationToken);
        if (fleet is null) return null;

        // Asked against the open set rather than re-stating its rule here, so the two can never drift apart.
        // ponytail: linear scan over the open fleets; a server's fleet directory is small, index it if that changes.
        if (ownerCharacterId is not null
            && !(await fleets.ListOpenAsync(cancellationToken)).Any(open => open.Id == fleetId))
            return null;

        var wings = new List<ApiFleetWing>();
        foreach (FleetWing wing in await fleets.ListWingsAsync(fleetId, cancellationToken))
        {
            IReadOnlyList<FleetSquad> squads = await fleets.ListSquadsAsync(wing.Id, cancellationToken);
            wings.Add(new ApiFleetWing(wing.Id, wing.Name,
                [.. squads.Select(squad => new ApiFleetSquad(squad.Id, squad.Name))]));
        }

        IReadOnlyList<FleetMember> members = await fleets.ListMembersAsync(fleetId, cancellationToken);
        string? compositionName = fleet.FleetCompositionId is { } compositionId
            ? (await compositions.GetAsync(compositionId, cancellationToken))?.Name
            : null;

        return new ApiFleetDetail(
            fleet.Id, fleet.Name, fleet.Description, fleet.CreatorCharacterId,
            fleet.State.ToString(), fleet.Activation.ToString(), fleet.Visibility.ToString(),
            fleet.FleetCompositionId, compositionName,
            wings,
            [.. members.Select(member => new ApiFleetMember(
                member.Id, member.CharacterId, member.WingId, member.SquadId, member.Role.ToString(),
                member.IsExternal, member.AssignedFit?.ShipTypeId, member.AssignedFit?.FitName))]);
    }

    /// <summary>The server's whole doctrine library, each row with the number of fleets coupled to it.</summary>
    public async Task<IReadOnlyList<ApiCompositionListItem>> GetCompositionsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FleetComposition> all = await compositions.ListAllAsync(cancellationToken);
        IReadOnlyDictionary<long, int> fleetCounts = await fleets.CountFleetsByCompositionIdsAsync(
            [.. all.Select(composition => composition.Id)], cancellationToken);

        return [.. all.Select(composition => new ApiCompositionListItem(
            composition.Id, composition.Name, composition.Description, composition.OwnerCharacterId,
            fleetCounts.TryGetValue(composition.Id, out int count) ? count : 0))];
    }

    /// <summary>One doctrine with its role-groups and fit-entries; null when it does not exist.</summary>
    public async Task<ApiCompositionDetail?> GetCompositionAsync(
        long compositionId, CancellationToken cancellationToken = default)
    {
        FleetCompositionGraph? graph = await compositions.GetGraphAsync(compositionId, cancellationToken);
        if (graph is null) return null;

        return new ApiCompositionDetail(
            graph.Composition.Id, graph.Composition.Name, graph.Composition.Description,
            graph.Composition.OwnerCharacterId,
            [.. graph.Roles.Select(role => new ApiCompositionRole(
                role.Role.Id, role.Role.RoleName, role.Role.GroupMinCount,
                [.. role.Entries.Select(entry => new ApiCompositionEntry(
                    entry.Id, entry.EntryMinCount, entry.Fit.ShipTypeId, entry.Fit.FitName))]))]);
    }
}
