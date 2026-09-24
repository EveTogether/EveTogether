using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Repositories;

/// <summary>
/// The read half of <see cref="IFittingRepository"/> (ET-383). Everything outside the fittings command handlers takes
/// this one: a library write outside a handler publishes no <c>FittingsChangedEvent</c>, so the fit lists miss it.
/// </summary>
public interface IFittingReader
{
    Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
    /// <summary>Finds a fitting by its local DB id, regardless of owner (fits are portable).</summary>
    Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default);

    /// <summary>Finds a local fit by its content fingerprint (owner-agnostic, 2026-06-04) — the dedup key for ESI
    /// import + download-from-server. Returns the existing fit (for the "duplicate of X" report) or null.</summary>
    Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);
}
