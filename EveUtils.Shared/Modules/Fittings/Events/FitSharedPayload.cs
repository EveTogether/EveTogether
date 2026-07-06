namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>Payload carried by <see cref="FitSharedEvent"/> over the remote event bus.</summary>
public sealed record FitSharedPayload(
    int EsiFittingId,
    string Name,
    int ShipTypeId,
    string RawJson,
    string SharedByCharacterName);
