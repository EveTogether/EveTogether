using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Repositories;

/// <summary>The local fit library: <see cref="IFittingReader"/> plus the writes, taken only by the fittings command
/// handlers (ET-383).</summary>
public interface IFittingRepository : IFittingReader
{
    Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default);

    /// <summary>One-time fill of <see cref="LocalFitting.ContentHash"/> for rows written before the column existed.</summary>
    Task BackfillContentHashesAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates a fit's user metadata (name, description, tags) without touching its modules or content hash,
    /// so editing never changes the fit's identity. No-op if the id is gone.</summary>
    Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default);

    Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default);
    /// <summary>Removes a fit from the local library by DB id.</summary>
    Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default);
}
