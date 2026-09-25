using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>
/// Resolves which <see cref="IAppraisalProvider"/> the user has chosen (ET-364). Every caller — the Appraisal tool
/// and the run, mining and consumables screens alike — goes through this instead of
/// <c>IServiceProvider.GetService&lt;IAppraisalProvider&gt;()</c>, which resolves to whichever provider was
/// registered last and ignores the user's choice entirely once a second provider exists.
///
/// The setting is read fresh on every call rather than cached, so nothing here ever holds a stale pick — the same
/// trade <c>SetSettingCommand</c>'s own signal exemption already makes.
/// </summary>
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
