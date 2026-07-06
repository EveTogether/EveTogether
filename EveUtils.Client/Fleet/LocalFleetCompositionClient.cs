using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Client.Fleet;

/// <summary>
/// <see cref="IFleetCompositionClient"/> for a client-only composition library: reads straight from the local
/// <see cref="IFleetCompositionRepository"/> and routes mutations through <see cref="ClientFleetService"/> (the SAME
/// Shared CQRS composition handlers), with no server or gRPC. A sibling of <see cref="LocalFleetClient"/>. New
/// compositions are client-only (<c>isClientOnly = true</c>) and owner-only by construction.
/// </summary>
public sealed class LocalFleetCompositionClient(
    ClientFleetService local, IFleetCompositionRepository repository, int ownerCharacterId) : IFleetCompositionClient
{
    public bool SharesFitsToServer => false;

    public async Task<IReadOnlyList<FleetCompositionInfo>> ListAsync()
    {
        var compositions = await repository.ListByOwnerAsync(ownerCharacterId);
        var fleetCounts = await local.CountFleetsByCompositionIdsAsync(compositions.Select(c => c.Id).ToList());
        return compositions.Select(c => MapComposition(c, FleetCountOf(fleetCounts, c.Id))).ToList();
    }

    // Local store holds a single owner, so the "all" list equals the owner's own (every one editable).
    public Task<IReadOnlyList<FleetCompositionInfo>> ListAllAsync() => ListAsync();

    public async Task<FleetCompositionDetail?> GetAsync(long compositionId)
    {
        var graph = await repository.GetGraphAsync(compositionId);
        return graph is null ? null : MapDetail(graph);
    }

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(string name, string? description) =>
        MapId(await local.CreateCompositionAsync(name, description, isClientOnly: true, ownerCharacterId));

    public async Task<(bool Ok, string Message)> EditAsync(long compositionId, string name, string? description) =>
        Map(await local.EditCompositionAsync(compositionId, name, description, ownerCharacterId));

    public async Task<(bool Ok, string Message)> DeleteAsync(long compositionId) =>
        Map(await local.DeleteCompositionAsync(compositionId, ownerCharacterId));

    public async Task<(bool Ok, string Message, long Id)> AddRoleAsync(long compositionId, string roleName, int? groupMinCount) =>
        MapId(await local.AddCompositionRoleAsync(compositionId, roleName, groupMinCount, ownerCharacterId));

    public async Task<(bool Ok, string Message)> EditRoleAsync(long roleId, string roleName, int? groupMinCount) =>
        Map(await local.EditCompositionRoleAsync(roleId, roleName, groupMinCount, ownerCharacterId));

    public async Task<(bool Ok, string Message)> RemoveRoleAsync(long roleId) =>
        Map(await local.RemoveCompositionRoleAsync(roleId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds) =>
        Map(await local.ReorderCompositionRolesAsync(compositionId, orderedRoleIds, ownerCharacterId));

    public async Task<(bool Ok, string Message, long Id)> AddEntryAsync(long roleId, FitReferenceInfo fit, int? entryMinCount) =>
        MapId(await local.AddCompositionEntryAsync(roleId, ToFit(fit), entryMinCount, ownerCharacterId));

    public async Task<(bool Ok, string Message)> EditEntryAsync(long entryId, int? entryMinCount) =>
        Map(await local.EditCompositionEntryAsync(entryId, entryMinCount, ownerCharacterId));

    public async Task<(bool Ok, string Message)> RemoveEntryAsync(long entryId) =>
        Map(await local.RemoveCompositionEntryAsync(entryId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds) =>
        Map(await local.ReorderCompositionEntriesAsync(roleId, orderedEntryIds, ownerCharacterId));

    private static FleetCompositionInfo MapComposition(FleetComposition c, int fleetCount = 0) =>
        new(c.Id, c.Name, c.Description, c.OwnerCharacterId, c.CreatedAt, c.UpdatedAt, FleetCount: fleetCount);

    private static int FleetCountOf(IReadOnlyDictionary<long, int> counts, long compositionId) =>
        counts.TryGetValue(compositionId, out var count) ? count : 0;

    private static FleetCompositionDetail MapDetail(FleetCompositionGraph graph) =>
        new(MapComposition(graph.Composition), graph.Roles.Select(MapRole).ToList());

    private static FleetCompositionRoleInfo MapRole(FleetCompositionRoleGraph roleGraph) =>
        new(roleGraph.Role.Id, roleGraph.Role.CompositionId, roleGraph.Role.RoleName,
            roleGraph.Role.GroupMinCount, roleGraph.Role.SortOrder, roleGraph.Entries.Select(MapEntry).ToList());

    private static FleetCompositionEntryInfo MapEntry(FleetCompositionEntry entry) =>
        new(entry.Id, entry.RoleId, entry.EntryMinCount, entry.SortOrder, MapFit(entry.Fit));

    private static FitReferenceInfo MapFit(FitReference fit) =>
        new(fit.ShipTypeId, fit.FitName, fit.RawJson, fit.ContentHash, fit.LocalFittingId, fit.ServerSharedFitId);

    private static FitReference ToFit(FitReferenceInfo info) => new()
    {
        ShipTypeId = info.ShipTypeId,
        FitName = info.FitName,
        RawJson = info.RawJson,
        ContentHash = info.ContentHash,
        LocalFittingId = info.LocalFittingId,
        ServerSharedFitId = info.ServerSharedFitId
    };

    private static (bool Ok, string Message) Map(Result result) => (result.IsSuccess, FirstMessage(result));

    private static (bool Ok, string Message, long Id) MapId(Result<long> result) =>
        (result.IsSuccess, FirstMessage(result), result.IsSuccess ? result.Value : 0);

    private static string FirstMessage(Result result) => result.Messages.FirstOrDefault()?.Text ?? "";
    private static string FirstMessage(Result<long> result) => result.Messages.FirstOrDefault()?.Text ?? "";
}
