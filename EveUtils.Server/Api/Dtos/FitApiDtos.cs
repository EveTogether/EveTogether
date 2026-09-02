namespace EveUtils.Server.Api.Dtos;

/// <summary>Server-wide shared fit; canonical ESI fitting JSON and contributor attribution are public so the library remains useful.</summary>
public sealed record ApiFit(
    int Id,
    int EsiFittingId,
    string Name,
    int ShipTypeId,
    string RawJson,
    string SharedByCharacterName,
    int SharedByCharacterId,
    DateTimeOffset SharedAt);
