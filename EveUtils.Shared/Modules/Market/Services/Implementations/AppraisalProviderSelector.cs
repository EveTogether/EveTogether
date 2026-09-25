using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Queries;

namespace EveUtils.Shared.Modules.Market.Services.Implementations;

/// <summary>See <see cref="IAppraisalProviderSelector"/>. Reads <see cref="SettingKey"/> through the dispatcher, the
/// same query the Settings screen and <c>ClipboardWatchService</c> already use, rather than taking the setting
/// repository directly — the read is the whole of what this needs from Settings.</summary>
public sealed class AppraisalProviderSelector(IEnumerable<IAppraisalProvider> providers, IDispatcher dispatcher)
    : IAppraisalProviderSelector, ISingletonService
{
    /// <summary>Settings key for the chosen provider's <see cref="IAppraisalProvider.Id"/>. Absent = the default.</summary>
    public const string SettingKey = "appraisal.provider";

    private const string Source = "Appraisal";

    public async Task<IAppraisalProvider?> SelectAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        var chosenId = settings.FirstOrDefault(setting => setting.Key == SettingKey)?.Value;

        foreach (var candidateId in new[] { chosenId, MarketPriceAppraisalProvider.ProviderId })
        {
            if (candidateId is null)
                continue;

            var candidate = providers.FirstOrDefault(provider => provider.Id == candidateId);
            if (candidate is not null && await candidate.IsAvailableAsync(cancellationToken))
                return candidate;
        }

        return providers.FirstOrDefault();
    }

    public async Task<Result<AppraisalOutcome>> AppraiseWithFallbackAsync(
        IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default)
    {
        var chosen = await SelectAsync(cancellationToken);
        if (chosen is null)
            return Result<AppraisalOutcome>.Failure(new ResultMessage(
                MessageSeverity.Warning, MessageCodes.NotFound, "No price source is available.", Source));

        var valued = await chosen.AppraiseAsync(lines, cancellationToken);
        if (valued.IsSuccess || chosen.Id == MarketPriceAppraisalProvider.ProviderId)
            return valued;

        var fallback = providers.FirstOrDefault(provider => provider.Id == MarketPriceAppraisalProvider.ProviderId);
        if (fallback is null)
            return valued;

        var fallbackValued = await fallback.AppraiseAsync(lines, cancellationToken);
        if (!fallbackValued.IsSuccess)
            return fallbackValued;

        return Result<AppraisalOutcome>.Success(fallbackValued.Value! with
        {
            PricingBasis = $"{chosen.DisplayName} unavailable, ESI average. {fallbackValued.Value!.PricingBasis}"
        });
    }
}
