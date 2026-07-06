using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Repositories;

public interface IFittingRepository
{
    Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
    /// <summary>Finds a fitting by its local DB id, regardless of owner (fits are portable).</summary>
    Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default);

    /// <summary>Finds a local fit by its content fingerprint (owner-agnostic, 2026-06-04) — the dedup key for ESI
    /// import + download-from-server. Returns the existing fit (for the "duplicate of X" report) or null.</summary>
    Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>One-time fill of <see cref="LocalFitting.ContentHash"/> for rows written before the column existed.</summary>
    Task BackfillContentHashesAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates a fit's user metadata (name, description, tags) without touching its modules or content hash,
    /// so editing never changes the fit's identity. No-op if the id is gone.</summary>
    Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default);

    Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default);
    /// <summary>Removes a fit from the local library by DB id.</summary>
    Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default);
}
