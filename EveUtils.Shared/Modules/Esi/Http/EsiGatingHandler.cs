using System.Net;
using System.Text;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Withholds non-essential ESI data calls while Tranquility is down — an observed failed <c>/status/</c> poll
/// (<see cref="IEsiAvailabilityState"/>) or the daily ~11:00 UTC maintenance window (<see cref="EsiDowntime"/>) —
/// so we do not hammer a dead API or burn the error limit. The <c>/status/</c> poll itself is always
/// let through so the client keeps detecting recovery. Sits just inside the cache handler: a fresh local-cache
/// hit is still served during downtime; only a call that would actually hit the network is gated.
/// </summary>
public sealed class EsiGatingHandler(IEsiAvailabilityState availability, TimeProvider time) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The /status/ poll must always go through so the poller can detect when ESI comes back.
        if (!EsiStatusEndpoint.IsStatusPoll(request.RequestUri) &&
            (!availability.IsUsable || EsiDowntime.IsScheduledWindow(time.GetUtcNow())))
            return Withheld();

        return await base.SendAsync(request, cancellationToken);
    }

    // A synthetic 503 tagged with the gate header so the pivot logs it at Debug (expected) and maps it onto
    // EsiErrorKind.Unavailable rather than a real server fault; the UI's downtime banner explains why.
    private static HttpResponseMessage Withheld()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"error\":\"EVE/ESI is in downtime — call withheld by the local gate.\"}",
                Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation(EsiGateHeaders.Withheld, "1");
        return response;
    }
}
