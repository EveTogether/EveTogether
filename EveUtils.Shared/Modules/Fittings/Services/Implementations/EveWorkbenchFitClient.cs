using System.Net.Http.Json;
using System.Text.Json;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Services.Implementations;

/// <summary>
/// Fetches a published EVE Workbench fit as an EFT block over the public API.
/// </summary>
public sealed class EveWorkbenchFitClient(IHttpClientFactory httpClientFactory)
    : IEveWorkbenchFitClient, ISingletonService
{
    public const string HttpClientName = "eveworkbench";
    public const string BaseUrl = "https://api.eveworkbench.com/";

    private const string Source = "Fittings";

    private sealed record EftResponse(string? Eft, bool Error, string? Message);

    public async Task<Result<string>> FetchEftAsync(Guid fitId, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            var response = await client.GetAsync($"v1/fits/{fitId:D}/eft", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return _Failed(MessageCodes.ServerError,
                    $"EVE Workbench could not return that fit (HTTP {(int)response.StatusCode}).");

            var payload = await response.Content.ReadFromJsonAsync<EftResponse>(cancellationToken);

            // A missing, unpublished or private fit still answers HTTP 200, with Error=true in the body; going
            // by the status code alone would import an empty fit. Those three cases are indistinguishable
            // from the outside, so one message has to cover all of them.
            if (payload is null || payload.Error || string.IsNullOrWhiteSpace(payload.Eft))
                return _Failed(MessageCodes.NotFound,
                    "That fit is not available on EVE Workbench — check the link, or ask its owner to publish it.");

            return Result<string>.Success(payload.Eft);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _Failed(MessageCodes.Timeout, "EVE Workbench did not answer in time — try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            return _Failed(MessageCodes.ServerError, $"Could not reach EVE Workbench ({ex.Message}).");
        }
        catch (JsonException)
        {
            return _Failed(MessageCodes.ParseError, "EVE Workbench returned a response this version cannot read.");
        }
    }

    private static Result<string> _Failed(string code, string text) =>
        Result<string>.Failure(new ResultMessage(MessageSeverity.Error, code, text, Source));
}
