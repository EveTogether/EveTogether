namespace EveUtils.Client.Esi;

/// <summary>How a known fit was selected, or why no single fit could be selected.</summary>
public enum ShipFitMatchReason
{
    NoFitFound,
    AmbiguousShipType,
    OnlyFitForShipType,
    ShipName,
    Manual,

    /// <summary>The player unlinked the fit: flying this ship without one is itself the answer, so the automatic
    /// match must not fill it back in.</summary>
    Detached,
}
