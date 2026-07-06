using System.Collections.Generic;
using System.Threading.Tasks;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The composition-library surface the Compositions window drives, abstracted over the transport so the SAME
/// window serves a server-shared library and a client-only one. A sibling of <see cref="IFleetClient"/> (the
/// per-fleet roster facade): implementations bind their context once (server address + acting character for
/// <see cref="ServerFleetCompositionClient"/>; the local repository + owner for <see cref="LocalFleetCompositionClient"/>),
/// so the methods take no server/character. Anti-splintering: one set of Shared CQRS handlers behind both.
/// </summary>
public interface IFleetCompositionClient
{
    /// <summary>True when saving through this client sends fits off the machine to a server (others can then view them),
    /// so the editor confirms the fit-share before persisting; false for the local-only library.</summary>
    bool SharesFitsToServer { get; }

    Task<IReadOnlyList<FleetCompositionInfo>> ListAsync();

    /// <summary>Every composition the source exposes, each with its per-character edit-state and owner name: on a
    /// server that is the whole server-wide library; on the local store it is the owner's own compositions.</summary>
    Task<IReadOnlyList<FleetCompositionInfo>> ListAllAsync();

    Task<FleetCompositionDetail?> GetAsync(long compositionId);

    Task<(bool Ok, string Message, long Id)> CreateAsync(string name, string? description);
    Task<(bool Ok, string Message)> EditAsync(long compositionId, string name, string? description);
    Task<(bool Ok, string Message)> DeleteAsync(long compositionId);

    Task<(bool Ok, string Message, long Id)> AddRoleAsync(long compositionId, string roleName, int? groupMinCount);
    Task<(bool Ok, string Message)> EditRoleAsync(long roleId, string roleName, int? groupMinCount);
    Task<(bool Ok, string Message)> RemoveRoleAsync(long roleId);
    Task<(bool Ok, string Message)> ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds);

    Task<(bool Ok, string Message, long Id)> AddEntryAsync(long roleId, FitReferenceInfo fit, int? entryMinCount);
    Task<(bool Ok, string Message)> EditEntryAsync(long entryId, int? entryMinCount);
    Task<(bool Ok, string Message)> RemoveEntryAsync(long entryId);
    Task<(bool Ok, string Message)> ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds);
}
