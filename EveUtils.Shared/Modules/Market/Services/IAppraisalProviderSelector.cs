using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>Resolves the <see cref="IAppraisalProvider"/> the user has chosen (ET-364), where a plain
/// <c>GetService&lt;IAppraisalProvider&gt;()</c> returns whichever provider registered last. The setting is read on
/// every call, so a pick never goes stale.</summary>
public interface IAppraisalProviderSelector
{
    /// <summary>The provider the user has chosen, or the default (<see cref="Implementations.MarketPriceAppraisalProvider"/>)
    /// when nothing is chosen, the chosen one is no longer installed, or it needs a key that is not configured.
    /// Null only when no provider at all is installed.</summary>
    Task<IAppraisalProvider?> SelectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Values through the chosen provider, falling back to the default provider when the chosen one fails — the
    /// rule the run, mining and consumables screens follow so a temporary EVE Workbench outage does not blank their
    /// figures. The standalone Appraisal tool does not use this: it calls <see cref="SelectAsync"/> and the chosen
    /// provider directly, since it exists to show exactly what that source answered, error included (ET-83).
    /// </summary>
    Task<Result<AppraisalOutcome>> AppraiseWithFallbackAsync(
        IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default);
}
