using System.Globalization;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;

namespace EveUtils.Shared.Modules.Market.Services.Implementations;

/// <summary>
/// Values a list from the hourly ESI price cache. That source is one global CCP average per type — no region, no
/// hub, no buy/sell split — so only <see cref="AppraisalPrice.Estimate"/> is ever filled, and the basis line says
/// as much rather than letting the figure pass for a Jita quote. Nothing is fetched here: the background refresh
/// fills the cache, this reads it.
/// </summary>
internal sealed class MarketPriceAppraisalProvider(IMarketPriceRepository prices) : IAppraisalProvider, ISingletonService
{
    public string Id => "market-prices";

    public string DisplayName => "ESI average price";

    public async Task<Result<AppraisalOutcome>> AppraiseAsync(
        IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default)
    {
        // Asked apart from the lookup below: "these items have no price" and "there are no prices at all" read the
        // same in a total of zero, and only the second one is the user's cue to wait for the hourly refresh.
        if (await prices.CountAsync(cancellationToken) == 0)
            return Result<AppraisalOutcome>.Failure(new ResultMessage(MessageSeverity.Warning,
                MessageCodes.PriceCacheEmpty,
                "No market prices have been cached yet. They are fetched from ESI once an hour — try again shortly.",
                nameof(MarketPriceAppraisalProvider)));

        var typeIds = lines.Select(line => line.TypeId).Distinct().ToList();
        IReadOnlyDictionary<int, double> averages = await prices.GetAveragePricesAsync(typeIds, cancellationToken);
        var snapshot = await prices.GetSnapshotTimeAsync(cancellationToken);

        List<AppraisalRow> rows = [.. lines.Select(line => new AppraisalRow(line,
            averages.TryGetValue(line.TypeId, out var average) ? new AppraisalPrice(average) : null))];

        return Result<AppraisalOutcome>.Success(new AppraisalOutcome(rows, [], _Basis(snapshot)));
    }

    private static string _Basis(DateTimeOffset? snapshot) =>
        "ESI average price — one CCP-wide figure per item, not a Jita buy or sell quote."
        + (snapshot is { } stamp
            ? $" Cached {stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC."
            : string.Empty);
}
