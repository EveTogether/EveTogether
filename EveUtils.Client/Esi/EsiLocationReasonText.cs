using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>
/// The one wording for why the ESI location watch has nothing to report, shared by every reader — the metrics
/// window, the fleet screen and the local API — so a pilot without the scope reads the same sentence everywhere
/// instead of each screen inventing its own (ET-96).
/// </summary>
public static class EsiLocationReasonText
{
    /// <summary>Null in, null out: no reason recorded is not itself a reason (nothing heard from the watch yet).</summary>
    public static string? Describe(EsiErrorKind? reason) => reason switch
    {
        null => null,
        EsiErrorKind.ScopeMissing => "no location access",
        EsiErrorKind.AuthRequired => "sign-in expired",
        _ => "location unavailable",
    };
}
