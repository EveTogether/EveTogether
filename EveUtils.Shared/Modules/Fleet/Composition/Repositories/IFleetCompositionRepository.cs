namespace EveUtils.Shared.Modules.Fleet.Composition.Repositories;

/// <summary>
/// Persistence for fleet compositions: the doctrine, its role-groups and their fit-entries. The
/// same Shared repository serves both hosts (a client-only composition lives in the client SQLite, a shared one
/// on the server). Authorization resolves an entry/role back to its composition's owner via
/// <see cref="GetEntryAsync"/> → <see cref="GetRoleAsync"/> → <see cref="GetAsync"/>, mirroring the fleet
/// wing/squad → fleet chain.
/// </summary>
public interface IFleetCompositionRepository
{
    // --- Composition ---

    Task<long> AddAsync(FleetComposition composition, CancellationToken cancellationToken = default);

    Task<FleetComposition?> GetAsync(long compositionId, CancellationToken cancellationToken = default);

    /// <summary>Persists changes to an existing composition (rename/describe). The entity is updated wholesale.</summary>
    Task UpdateAsync(FleetComposition composition, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a composition; its roles and their entries cascade with it (FK).</summary>
    Task DeleteAsync(long compositionId, CancellationToken cancellationToken = default);

    /// <summary>The compositions a character owns.</summary>
    Task<IReadOnlyList<FleetComposition>> ListByOwnerAsync(int ownerCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Every composition on the server, regardless of owner (server-wide library; the per-character
    /// edit-state is layered on top by the authorizer). Client-only stores hold a single owner, so it equals
    /// <see cref="ListByOwnerAsync"/> there.</summary>
    Task<IReadOnlyList<FleetComposition>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole composition — header + role-groups + their fit-entries, all in sort order — for the
    /// editor/transport read. Null if the composition does not exist.</summary>
    Task<FleetCompositionGraph?> GetGraphAsync(long compositionId, CancellationToken cancellationToken = default);

    // --- Role-groups ---

    Task<long> AddRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default);

    Task<FleetCompositionRole?> GetRoleAsync(long roleId, CancellationToken cancellationToken = default);

    Task UpdateRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default);

    /// <summary>Removes a role-group; its entries cascade with it (FK).</summary>
    Task DeleteRoleAsync(long roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetCompositionRole>> ListRolesAsync(long compositionId, CancellationToken cancellationToken = default);

    /// <summary>Sets each role's <see cref="FleetCompositionRole.SortOrder"/> to its position in the given order.
    /// Ids that do not belong to the composition are ignored.</summary>
    Task ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds, CancellationToken cancellationToken = default);

    // --- Fit-entries ---

    Task<long> AddEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>A single fit-entry by its primary key; the owned <see cref="FleetCompositionEntry.Fit"/> snapshot loads with it.</summary>
    Task<FleetCompositionEntry?> GetEntryAsync(long entryId, CancellationToken cancellationToken = default);

    Task UpdateEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes a single fit-entry by its primary key. No-op if it is gone.</summary>
    Task DeleteEntryAsync(long entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetCompositionEntry>> ListEntriesAsync(long roleId, CancellationToken cancellationToken = default);

    /// <summary>Sets each entry's <see cref="FleetCompositionEntry.SortOrder"/> to its position in the given order.
    /// Ids that do not belong to the role are ignored.</summary>
    Task ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds, CancellationToken cancellationToken = default);
}
