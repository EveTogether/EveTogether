using EveUtils.Shared.Modules.Fittings.Entities;

namespace EveUtils.Shared.Modules.Fittings.Repositories;

/// <summary>
/// The read half of <see cref="ISharedFitRepository"/> (ET-383). Everything outside the fittings command handlers
/// takes this one, so a shared fit is only ever stored or removed by a handler that signals it.
/// </summary>
public interface ISharedFitReader
{
    Task<SharedFit?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedFit>> ListAsync(CancellationToken cancellationToken = default);
}
