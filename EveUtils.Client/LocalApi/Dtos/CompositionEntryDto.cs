namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>One fit-entry in a composition role: the ship type + resolved name, the fit name and an optional minimum count.</summary>
public sealed record CompositionEntryDto(
    long Id,
    int? EntryMinCount,
    int ShipTypeId,
    string ShipName,
    string FitName);
