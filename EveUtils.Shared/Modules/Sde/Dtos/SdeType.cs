namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A single EVE inventory type (item/ship/module/charge) as stored in the read-only SDE store.</summary>
public sealed record SdeType(
    int TypeId,
    int GroupId,
    string Name,
    bool Published,
    double Mass,
    double Volume,
    double Capacity,
    int? MarketGroupId);
