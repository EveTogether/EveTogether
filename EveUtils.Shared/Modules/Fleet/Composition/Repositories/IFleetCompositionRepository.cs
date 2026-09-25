namespace EveUtils.Shared.Modules.Fleet.Composition.Repositories;

/// <summary>
/// Persistence for fleet compositions: the doctrine, its role-groups and their fit-entries. The
/// same Shared repository serves both hosts (a client-only composition lives in the client SQLite, a shared one
/// on the server). Authorization resolves an entry/role back to its composition's owner via
/// <see cref="IFleetCompositionReader.GetEntryAsync"/> → <see cref="IFleetCompositionReader.GetRoleAsync"/> →
/// <see cref="IFleetCompositionReader.GetAsync"/>, mirroring the fleet wing/squad → fleet chain. Only the composition
/// command handlers take this one (ET-383); everything else reads through <see cref="IFleetCompositionReader"/>.
/// </summary>
public interface IFleetCompositionRepository : IFleetCompositionReader
{
    // --- Composition ---

    Task<long> AddAsync(FleetComposition composition, CancellationToken cancellationToken = default);

    /// <summary>Persists changes to an existing composition (rename/describe). The entity is updated wholesale.</summary>
    Task UpdateAsync(FleetComposition composition, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a composition; its roles and their entries cascade with it (FK).</summary>
    Task DeleteAsync(long compositionId, CancellationToken cancellationToken = default);

    // --- Role-groups ---

    Task<long> AddRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default);

    Task UpdateRoleAsync(FleetCompositionRole role, CancellationToken cancellationToken = default);

    /// <summary>Removes a role-group; its entries cascade with it (FK).</summary>
    Task DeleteRoleAsync(long roleId, CancellationToken cancellationToken = default);

    /// <summary>Sets each role's <see cref="FleetCompositionRole.SortOrder"/> to its position in the given order.
    /// Ids that do not belong to the composition are ignored.</summary>
    Task ReorderRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds, CancellationToken cancellationToken = default);

    // --- Fit-entries ---

    Task<long> AddEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Persists an entry's per-fit minimum, sort order and skill minimums (the owned rows are made to match
    /// <see cref="FleetCompositionEntry.SkillMinimums"/>). The fit snapshot never changes. No-op if it is gone.</summary>
    Task UpdateEntryAsync(FleetCompositionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes a single fit-entry by its primary key. No-op if it is gone.</summary>
    Task DeleteEntryAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Sets each entry's <see cref="FleetCompositionEntry.SortOrder"/> to its position in the given order.
    /// Ids that do not belong to the role are ignored.</summary>
    Task ReorderEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds, CancellationToken cancellationToken = default);
}
