using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A stand-in <see cref="IFleetCompositionClient"/> that records the persist calls the editor replays, so a test can
/// assert whether a save actually shared anything. <see cref="SharesFitsToServer"/> is settable to model a server-backed
/// library (true) versus the local one (false), for the composition editor opsec gate.
/// </summary>
public sealed class RecordingCompositionClient(bool sharesFitsToServer) : IFleetCompositionClient
{
    private long _nextId;

    public bool SharesFitsToServer { get; } = sharesFitsToServer;

    public int CreateCount { get; private set; }
    public List<FitReferenceInfo> AddedFits { get; } = [];

    /// <summary>Compositions owned by the acting character — what <see cref="ListAsync"/> (ListByOwner on a server) returns.</summary>
    public List<FleetCompositionInfo> OwnCompositions { get; } = [];

    /// <summary>Server-wide compositions authored by others — only surfaced by the whole-library <see cref="ListAllAsync"/>.</summary>
    public List<FleetCompositionInfo> ServerWideCompositions { get; } = [];

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAsync() =>
        Task.FromResult<IReadOnlyList<FleetCompositionInfo>>(OwnCompositions);

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAllAsync() =>
        Task.FromResult<IReadOnlyList<FleetCompositionInfo>>([.. OwnCompositions, .. ServerWideCompositions]);

    public Task<FleetCompositionDetail?> GetAsync(long compositionId) => Task.FromResult<FleetCompositionDetail?>(null);

    public Task<(bool Ok, string Message, long Id)> CreateAsync(string name, string? description)
    {
        CreateCount++;
        return Task.FromResult((true, string.Empty, ++_nextId));
    }

    public Task<(bool Ok, string Message)> EditAsync(long compositionId, string name, string? description) => Ok();
    public Task<(bool Ok, string Message)> DeleteAsync(long compositionId) => Ok();

    public Task<(bool Ok, string Message, long Id)> AddRoleAsync(long compositionId, string roleName, int? groupMinCount) =>
        Task.FromResult((true, string.Empty, ++_nextId));

    public Task<(bool Ok, string Message)> EditRoleAsync(long roleId, string roleName, int? groupMinCount) => Ok();
    public Task<(bool Ok, string Message)> RemoveRoleAsync(long roleId) => Ok();
    public Task<(bool Ok, string Message)> ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds) => Ok();

    public Task<(bool Ok, string Message, long Id)> AddEntryAsync(long roleId, FitReferenceInfo fit, int? entryMinCount)
    {
        AddedFits.Add(fit);
        return Task.FromResult((true, string.Empty, ++_nextId));
    }

    public Task<(bool Ok, string Message)> EditEntryAsync(long entryId, int? entryMinCount) => Ok();
    public Task<(bool Ok, string Message)> RemoveEntryAsync(long entryId) => Ok();
    public Task<(bool Ok, string Message)> ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds) => Ok();

    private static Task<(bool Ok, string Message)> Ok() => Task.FromResult((true, string.Empty));
}
