namespace EveUtils.Client.Esi;

/// <summary>How a known fit was selected, or why no single fit could be selected.</summary>
public enum ShipFitMatchReason
{
    NoFitFound,
    AmbiguousShipType,
    OnlyFitForShipType,
    ShipName,
    Manual,
}
