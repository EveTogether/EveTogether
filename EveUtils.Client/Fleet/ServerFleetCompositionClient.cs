using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Transport;

namespace EveUtils.Client.Fleet;

/// <summary>
/// <see cref="IFleetCompositionClient"/> over the gRPC transport for a server-shared composition library: binds the
/// server address + acting character once, then delegates every call to the wrapped <see cref="IFleetTransportClient"/>.
/// New compositions are server-shared (<c>isClientOnly = false</c>); the server gates mutations owner-or-manage.
/// </summary>
public sealed class ServerFleetCompositionClient(IFleetTransportClient transport, string serverAddress, int actingCharacterId)
    : IFleetCompositionClient
{
    public bool SharesFitsToServer => true;

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAsync() =>
        transport.ListMyFleetCompositionsAsync(serverAddress, actingCharacterId);

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAllAsync() =>
        transport.ListAllFleetCompositionsAsync(serverAddress, actingCharacterId);

    public Task<FleetCompositionDetail?> GetAsync(long compositionId) =>
        transport.GetFleetCompositionAsync(serverAddress, compositionId, actingCharacterId);

    public Task<(bool Ok, string Message, long Id)> CreateAsync(string name, string? description) =>
        transport.CreateFleetCompositionAsync(serverAddress, name, description, isClientOnly: false, actingCharacterId);

    public Task<(bool Ok, string Message)> EditAsync(long compositionId, string name, string? description) =>
        transport.EditFleetCompositionAsync(serverAddress, compositionId, name, description, actingCharacterId);

    public Task<(bool Ok, string Message)> DeleteAsync(long compositionId) =>
        transport.DeleteFleetCompositionAsync(serverAddress, compositionId, actingCharacterId);

    public Task<(bool Ok, string Message, long Id)> AddRoleAsync(long compositionId, string roleName, int? groupMinCount) =>
        transport.AddFleetCompositionRoleAsync(serverAddress, compositionId, roleName, groupMinCount, actingCharacterId);

    public Task<(bool Ok, string Message)> EditRoleAsync(long roleId, string roleName, int? groupMinCount) =>
        transport.EditFleetCompositionRoleAsync(serverAddress, roleId, roleName, groupMinCount, actingCharacterId);

    public Task<(bool Ok, string Message)> RemoveRoleAsync(long roleId) =>
        transport.RemoveFleetCompositionRoleAsync(serverAddress, roleId, actingCharacterId);

    public Task<(bool Ok, string Message)> ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds) =>
        transport.ReorderFleetCompositionRolesAsync(serverAddress, compositionId, orderedRoleIds, actingCharacterId);

    public Task<(bool Ok, string Message, long Id)> AddEntryAsync(long roleId, FitReferenceInfo fit, int? entryMinCount) =>
        transport.AddFleetCompositionEntryAsync(serverAddress, roleId, fit, entryMinCount, actingCharacterId);

    public Task<(bool Ok, string Message)> EditEntryAsync(long entryId, int? entryMinCount) =>
        transport.EditFleetCompositionEntryAsync(serverAddress, entryId, entryMinCount, actingCharacterId);

    public Task<(bool Ok, string Message)> RemoveEntryAsync(long entryId) =>
        transport.RemoveFleetCompositionEntryAsync(serverAddress, entryId, actingCharacterId);

    public Task<(bool Ok, string Message)> ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds) =>
        transport.ReorderFleetCompositionEntriesAsync(serverAddress, roleId, orderedEntryIds, actingCharacterId);
}
