using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Esi;

/// <summary>Shared read-only current-ship cache; callers never trigger an ESI request.</summary>
public interface IShipFitDetectionService
{
    ShipFitDetectionReading GetReading(int characterId);
    Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default);
}
