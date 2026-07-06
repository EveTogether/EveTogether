using System.Net.Http.Headers;
using EveUtils.Shared.App;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Outermost handler in the ESI chain (Deel 6). Centralises the headers every ESI call must carry so no
/// call can go out missing one (the earlier source of a 400): a descriptive User-Agent, the pinned
/// <c>X-Compatibility-Date</c> with an optional per-call override via
/// <see cref="EsiRequestOptions.CompatibilityDate"/>, and <c>Accept: application/json</c>. An explicitly
/// set header is never overwritten. The compatibility date is only added for ESI data calls, not the SSO
/// token endpoint.
/// </summary>
public sealed class EsiHeaderHandler(IRuntimeContext runtime) : DelegatingHandler
{
    private const string CompatibilityDateHeader = "X-Compatibility-Date";
    private const string EsiDataHost = "esi.evetech.net";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent(runtime.Host));

        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (request.RequestUri is { Host: EsiDataHost } && !request.Headers.Contains(CompatibilityDateHeader))
        {
            var date = request.Options.TryGetValue(EsiRequestOptions.CompatibilityDate, out var overrideDate)
                       && !string.IsNullOrEmpty(overrideDate)
                ? overrideDate
                : EsiEndpoints.CompatibilityDate;
            request.Headers.TryAddWithoutValidation(CompatibilityDateHeader, date);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
