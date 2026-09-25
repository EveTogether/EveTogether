using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;

namespace EveUtils.Shared.Modules.Market.Services.Implementations;

/// <summary>
/// Values a list against EVE Workbench's keyed appraisal endpoint (ET-364): Jita 4-4 station prices, authenticated
/// with the user's own personal access token (header <c>Character-Access-Token</c>) rather than an application key,
/// which needs an IP allowlist a desktop app cannot offer. <see cref="AppraisalPrice.Estimate"/> is the sell price,
/// matching EVE Workbench's own default (<c>type=1</c>) appraisal.
///
/// The token never appears in a log line, a <see cref="Result{T}"/> message or an exception's text — every failure
/// path below names the HTTP outcome, never the request that produced it.
/// </summary>
public sealed class EveWorkbenchAppraisalProvider(IHttpClientFactory httpClientFactory, IEveWorkbenchKeyStore keyStore)
    : IAppraisalProvider
{
    /// <summary>The provider's <see cref="IAppraisalProvider.Id"/>, and also the setting value that selects it.</summary>
    public const string ProviderId = "eveworkbench";

    private const string Source = "Appraisal";
    private const string RequestPath = "v1/appraisal?station=60003760&type=1&persist=false"; // Jita 4-4

    public string Id => ProviderId;

    public string DisplayName => "EVE Workbench";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrEmpty(await keyStore.GetTokenAsync(cancellationToken));

    public async Task<Result<AppraisalOutcome>> AppraiseAsync(
        IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default)
    {
        var token = await keyStore.GetTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
            return _Failed(MessageCodes.AuthRequired,
                "No EVE Workbench personal access token is configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, RequestPath)
        {
            Content = new StringContent(
                string.Join('\n', lines.Select(line => $"{line.Name}\t{line.Quantity}")), Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("Character-Access-Token", token);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(EveWorkbenchFitClient.HttpClientName)
                .SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _Failed(MessageCodes.Timeout, "EVE Workbench did not answer in time — try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            return _Failed(MessageCodes.ServerError, $"Could not reach EVE Workbench ({ex.Message}).");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return _Failed(MessageCodes.AuthRequired,
                "EVE Workbench rejected the personal access token — check it in Settings.");
        if (!response.IsSuccessStatusCode)
            return _Failed(MessageCodes.ServerError,
                $"EVE Workbench could not value this listing (HTTP {(int)response.StatusCode}).");

        EwbAppraisalResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<EwbAppraisalResponse>(cancellationToken);
        }
        catch (JsonException)
        {
            return _Failed(MessageCodes.ParseError, "EVE Workbench returned a response this version cannot read.");
        }

        if (payload is null || payload.Error)
            return _Failed(MessageCodes.EsiFailed, payload?.Message ?? "EVE Workbench could not value this listing.");

        // Matched by type id, not by position: EWB's own parser silently drops a line it cannot read, so a request
        // line with no matching response item is the signal that it was dropped, not that it priced at nothing.
        var byTypeId = (payload.Items ?? []).GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => group.First());
        List<AppraisalRow> rows = [];
        List<string> unresolved = [];
        foreach (var line in lines)
        {
            if (byTypeId.TryGetValue(line.TypeId, out var item))
                rows.Add(new AppraisalRow(line, new AppraisalPrice(item.SellPrice, item.BuyPrice, item.SellPrice)));
            else
                unresolved.Add(line.Name);
        }

        return Result<AppraisalOutcome>.Success(
            new AppraisalOutcome(rows, unresolved, "EVE Workbench — Jita 4-4 station sell price."));
    }

    private static Result<AppraisalOutcome> _Failed(string code, string text) =>
        Result<AppraisalOutcome>.Failure(new ResultMessage(MessageSeverity.Error, code, text, Source));

    private sealed record EwbAppraisalResponse(Guid? Id, List<EwbItem>? Items, int ItemCount, bool Error, string? Message);

    private sealed record EwbItem(int TypeId, long Amount, string? Name, double Volume, double BuyPrice, double SellPrice);
}
