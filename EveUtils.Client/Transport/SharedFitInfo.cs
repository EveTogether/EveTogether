using System;

namespace EveUtils.Client.Transport;

/// <summary>A fit available on the server, as fetched for browsing/downloading to local.</summary>
public sealed record SharedFitInfo(
    int ServerId,
    int EsiFittingId,
    string Name,
    int ShipTypeId,
    string RawJson,
    string SharedByCharacterName,
    int SharedByCharacterId,
    DateTimeOffset SharedAt = default);
