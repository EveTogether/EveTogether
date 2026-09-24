using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Repositories;

/// <summary>A server's shared fit library: <see cref="ISharedFitReader"/> plus the writes, taken only by the fittings
/// command handlers (ET-383).</summary>
public interface ISharedFitRepository : ISharedFitReader
{
    Task AddAsync(SharedFit fit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the fit unless one with the same content already exists (content-hash dedup, owner/ESI-id-agnostic,
    /// 2026-06-04). Computes + stamps <see cref="SharedFit.ContentHash"/>. Returns <c>null</c> when it was added,
    /// or the existing matched fit when it was a duplicate (so the caller can report which fit it matched).
    /// </summary>
    Task<SharedFit?> AddOrMatchAsync(SharedFit fit, CancellationToken cancellationToken = default);

    /// <summary>Removes a shared fit from the server library by its DB id. True if it existed.</summary>
    Task<bool> RemoveAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>One-time fill of <see cref="SharedFit.ContentHash"/> for rows written before the column existed.</summary>
    Task BackfillContentHashesAsync(CancellationToken cancellationToken = default);
}
