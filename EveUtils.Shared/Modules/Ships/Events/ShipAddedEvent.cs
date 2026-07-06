using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Ships.Dtos;

namespace EveUtils.Shared.Modules.Ships.Events;

/// <summary>
/// Published by <see cref="Commands.AddShipCommandHandler"/> after a ship has been added. Carries
/// the <see cref="ShipDto"/> as its payload plus the shared event metadata (see
/// <see cref="IntegrationEvent{T}"/>): subscribers get <c>Data</c>, <c>CharacterId</c> and
/// <c>Timestamp</c> in the same shape whether the event is delivered locally or remotely.
/// </summary>
public sealed class ShipAddedEvent(ShipDto data, int? characterId = null)
    : IntegrationEvent<ShipDto>(data, characterId)
{
    public override string EventType => "ships.added";
}
