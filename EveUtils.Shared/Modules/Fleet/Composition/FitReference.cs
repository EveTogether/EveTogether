namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A self-contained snapshot of a fit, embedded as owned columns on its owner. It carries everything
/// needed to render the fit detail (<see cref="RawJson"/> deserialises straight into an <c>EsiFitting</c>), so a
/// composition entry or an assigned member fit never breaks when the source library fit is gone, and the snapshot
/// stays server-agnostic and shareable. <see cref="ContentHash"/> is the order-independent dedup/bridge key
/// (also the future external-import key). The origin ids are optional "jump to source" hints.
/// </summary>
public sealed class FitReference
{
    public int ShipTypeId { get; set; }
    public string FitName { get; set; } = string.Empty;

    /// <summary>Verbatim ESI fitting JSON — deserialises directly to an <c>EsiFitting</c> for the fit detail.</summary>
    public string RawJson { get; set; } = "{}";

    /// <summary>Order-independent fingerprint of the fit, shared with <c>LocalFitting</c>/<c>SharedFit</c>.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Origin hint (optional): the local-library fit this snapshot was taken from, if any.</summary>
    public int? LocalFittingId { get; set; }

    /// <summary>Origin hint (optional): the server shared fit this snapshot was taken from, if any.</summary>
    public int? ServerSharedFitId { get; set; }
}
