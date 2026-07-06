namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>A format-neutral parsed fit before SDE resolution: either a ship name (EFT) or a ship typeId (DNA).</summary>
internal sealed record RawFit(
    string? ShipName,
    int? ShipTypeId,
    string FitName,
    IReadOnlyList<RawFitItem> Items);
