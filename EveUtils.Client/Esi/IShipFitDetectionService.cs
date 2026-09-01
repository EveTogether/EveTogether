using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Esi;

/// <summary>Shared read-only current-ship cache; callers never trigger an ESI request.</summary>
public interface IShipFitDetectionService
{
    ShipFitDetectionReading GetReading(int characterId);
    Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default);

    /// <summary>Unlink the current ship's fit. Stored like a manual choice rather than held by the caller, so closing
    /// and reopening a window mid-run cannot quietly put the fit back.</summary>
    Task<Result> DetachFitAsync(int characterId, CancellationToken cancellationToken = default);
}
