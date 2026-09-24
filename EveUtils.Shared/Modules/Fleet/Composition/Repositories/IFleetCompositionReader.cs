namespace EveUtils.Shared.Modules.Fleet.Composition.Repositories;

/// <summary>
/// The read half of <see cref="IFleetCompositionRepository"/> (ET-383). Everything outside the composition command
/// handlers takes this one: a composition write outside a handler publishes no <c>CompositionChangedEvent</c>, so no
/// open library or editor hears it.
/// </summary>
public interface IFleetCompositionReader
{
    Task<FleetComposition?> GetAsync(long compositionId, CancellationToken cancellationToken = default);

    /// <summary>The compositions a character owns.</summary>
    Task<IReadOnlyList<FleetComposition>> ListByOwnerAsync(int ownerCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Every composition on the server, regardless of owner (server-wide library; the per-character
    /// edit-state is layered on top by the authorizer). Client-only stores hold a single owner, so it equals
    /// <see cref="ListByOwnerAsync"/> there.</summary>
    Task<IReadOnlyList<FleetComposition>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole composition — header + role-groups + their fit-entries, all in sort order — for the
    /// editor/transport read. Null if the composition does not exist.</summary>
    Task<FleetCompositionGraph?> GetGraphAsync(long compositionId, CancellationToken cancellationToken = default);

    Task<FleetCompositionRole?> GetRoleAsync(long roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetCompositionRole>> ListRolesAsync(long compositionId, CancellationToken cancellationToken = default);

    /// <summary>A single fit-entry by its primary key; the owned <see cref="FleetCompositionEntry.Fit"/> snapshot loads with it.</summary>
    Task<FleetCompositionEntry?> GetEntryAsync(long entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetCompositionEntry>> ListEntriesAsync(long roleId, CancellationToken cancellationToken = default);
}
