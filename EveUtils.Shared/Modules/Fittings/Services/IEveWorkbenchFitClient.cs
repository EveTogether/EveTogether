using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Services;

/// <summary>
/// Read-only access to EVE Workbench's public fit endpoint, one call per explicit user action.
/// EVE Together never depends on it being reachable: only the import itself fails when it is not.
/// </summary>
public interface IEveWorkbenchFitClient
{
    /// <summary>
    /// <c>GET /v1/fits/{fitId}/eft</c> — the fit as an EFT block, no authentication.
    /// A failure always carries a user-readable message (unknown or private fit, unreachable, timeout).
    /// </summary>
    Task<Result<string>> FetchEftAsync(Guid fitId, CancellationToken cancellationToken = default);
}
