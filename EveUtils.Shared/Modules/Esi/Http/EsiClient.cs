using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// The one ESI pivot. Runs the pre-flight (scope + token via <see cref="IEsiTokenProvider"/>) for
/// authed calls, sends through the injected <see cref="HttpClient"/> (which carries the handler chain —
/// header/cache/rate-limit/retry — once wired), then maps the response onto a uniform
/// <see cref="EsiResult{T}"/> per ESI-Reference §9. Callers never see raw HTTP.
/// </summary>
public sealed class EsiClient(
    IHttpClientFactory httpClientFactory,
    IEsiTokenProvider tokenProvider,
    IEsiOutageDetector outageDetector,
    ILogger<EsiClient> logger) : IEsiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
    {
        string? bearer = null;
        if (request.CharacterId is { } characterId)
        {
            var authorization = await tokenProvider
                .AuthorizeAsync(characterId, request.Scopes, cancellationToken);

            switch (authorization.Outcome)
            {
                case EsiAuthOutcome.ScopeMissing:
                    return EsiResult<T>.Fail(EsiError.Of(
                        EsiErrorKind.ScopeMissing,
                        $"Character {characterId} is missing the required scope '{authorization.MissingScope}'."));
                case EsiAuthOutcome.AuthRequired:
                    return EsiResult<T>.Fail(EsiError.Of(
                        EsiErrorKind.AuthRequired,
                        $"Character {characterId} needs to re-authenticate.",
                        httpStatus: 401));
                default:
                    bearer = authorization.AccessToken;
                    break;
            }
        }

        using var message = BuildRequest(request, bearer);

        var httpClient = httpClientFactory.CreateClient(EsiHttpClients.Data);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("ESI {Method} {Path} timed out.", request.Method, request.Path);
            outageDetector.RecordServerFailure();
            return EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.Timeout, $"ESI {request.Method} {request.Path} timed out."));
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Network error on ESI {Method} {Path}.", request.Method, request.Path);
            outageDetector.RecordServerFailure();
            return EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.Network, $"Network error contacting ESI: {ex.Message}"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                outageDetector.RecordSuccess();
                return Deserialize<T>(request, response, body);
            }

            var error = MapError(request, response, body);
            RecordOutcome(error.Kind);
            var status = (int)response.StatusCode;
            // A 404 means the resource isn't there — an expected outcome the caller handles via EsiErrorKind.NotFound
            // (e.g. "character is not in a fleet"), not a transport/server failure. Log it at Warning so it stays
            // visible in the in-app log without flooding the error category — unless the caller flagged the 404 as
            // routine (ExpectedNotFound, e.g. a 60s self-report poll), in which case Debug keeps the log clean.
            if (status == 404)
            {
                if (request.ExpectedNotFound)
                    logger.LogDebug(
                        "ESI {Method} {Path} returned 404 {Reason} (expected). Body: {Body}",
                        request.Method, request.Path, response.ReasonPhrase, Trim(body));
                else
                    logger.LogWarning(
                        "ESI {Method} {Path} returned 404 {Reason}. Body: {Body}",
                        request.Method, request.Path, response.ReasonPhrase, Trim(body));
            }
            // A call the local gate withheld because ESI is down never reached the network — it's expected and the
            // downtime banner already explains it, so keep it at Debug rather than crowding the error log.
            else if (response.Headers.Contains(EsiGateHeaders.Withheld))
                logger.LogDebug(
                    "ESI {Method} {Path} withheld by the local gate — ESI is down.", request.Method, request.Path);
            // A failed /status/ poll IS the "ESI is down" detection signal (the status service logs the transition
            // once); logging every poll as an error would just spam the log during an outage.
            else if (EsiStatusEndpoint.IsStatusPath(request.Path))
                logger.LogDebug(
                    "ESI status poll returned {Status} {Reason} — Tranquility appears down.",
                    status, response.ReasonPhrase);
            else
                logger.LogError(
                    "ESI {Method} {Path} failed: {Status} {Reason}. Body: {Body}",
                    request.Method, request.Path, status, response.ReasonPhrase, Trim(body));
            return EsiResult<T>.Fail(error);
        }
    }

    // Feed the outage detector: a server-side failure counts toward a suspected outage; any other real ESI response
    // (4xx/429 — ESI answered, just per-call) clears the run; a gate-withheld call is ignored (ESI already known down).
    private void RecordOutcome(EsiErrorKind kind)
    {
        switch (kind)
        {
            case EsiErrorKind.ServerError or EsiErrorKind.Timeout or EsiErrorKind.Network:
                outageDetector.RecordServerFailure();
                break;
            case EsiErrorKind.Unavailable:
                break;
            default:
                outageDetector.RecordSuccess();
                break;
        }
    }

    private static HttpRequestMessage BuildRequest(EsiRequest request, string? bearer)
    {
        var message = new HttpRequestMessage(request.Method, $"{EsiEndpoints.PublicDataBaseUrl}{request.Path}");

        // User-Agent, X-Compatibility-Date and Accept are set centrally by EsiHeaderHandler (Deel 6).
        if (bearer is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (request.CompatibilityDateOverride is { } compatDate)
            message.Options.Set(EsiRequestOptions.CompatibilityDate, compatDate);

        // Rate-limit bucket identity (§4b): authed calls are per app:character, public calls per IP.
        message.Options.Set(
            EsiRequestOptions.BucketKey,
            request.CharacterId is { } id ? $"app:{id}" : "ip");

        // Per-endpoint metrics dimension: the route template (ids collapsed), so calls aggregate by endpoint.
        message.Options.Set(EsiRequestOptions.EndpointKey, EsiEndpointKey.Normalize(request.Path));

        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        return message;
    }

    private EsiResult<T> Deserialize<T>(EsiRequest request, HttpResponseMessage response, string body)
    {
        var fromCache = response.Headers.TryGetValues(EsiCacheHeaders.FromCache, out _);

        // 204/empty body — a successful no-content call (e.g. a DELETE) has nothing to deserialize.
        if (string.IsNullOrWhiteSpace(body))
            return EsiResult<T>.Ok(default!, fromCache);

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is null)
                return EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ParseError, "ESI returned a null body for a success status."));
            return EsiResult<T>.Ok(value, fromCache);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse ESI {Path} body: {Body}", request.Path, Trim(body));
            return EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ParseError, $"Could not parse the ESI response: {ex.Message}"));
        }
    }

    /// <summary>Maps a non-success status + (defensively parsed) error body onto a structured error (§9).</summary>
    private static EsiError MapError(EsiRequest request, HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var message = ExtractErrorMessage(body) ?? $"ESI {request.Method} {request.Path} returned {status}.";

        // The local gate's synthetic 503 (ESI down) — distinguish it from a real server 503 so callers can treat it
        // as "skip, ESI is down" rather than a server fault.
        if (response.Headers.Contains(EsiGateHeaders.Withheld))
            return EsiError.Of(EsiErrorKind.Unavailable, message, status);

        return status switch
        {
            400 or 422 => EsiError.Of(EsiErrorKind.BadRequest, message, status),
            401 => EsiError.Of(EsiErrorKind.AuthRequired, message, status),
            // Pre-flight already blocks a known-missing scope, so a real 403 means the scope was present
            // but ESI still refuses (corp role/standing/structure access) — SCOPE_FORBIDDEN (§9 choice A).
            403 => EsiError.Of(EsiErrorKind.ScopeForbidden, message, status),
            404 => EsiError.Of(EsiErrorKind.NotFound, message, status),
            420 => EsiError.Of(EsiErrorKind.RateLimited, message, status,
                ErrorLimitReset(response), EsiRateLimitKind.ErrorLimit),
            429 => EsiError.Of(EsiErrorKind.RateLimited, message, status,
                response.Headers.RetryAfter?.Delta, EsiRateLimitKind.Bucket),
            504 => EsiError.Of(EsiErrorKind.Timeout, message, status),
            >= 500 => EsiError.Of(EsiErrorKind.ServerError, message, status),
            _ => EsiError.Of(EsiErrorKind.BadRequest, message, status)
        };
    }

    /// <summary>
    /// ESI data-API error bodies are a single <c>{"error": "..."}</c> field (§9); 429/HTML/empty bodies
    /// are tolerated by returning null so the caller falls back to a status-based message.
    /// </summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException)
        {
            // Non-JSON (HTML proxy page, empty) — fall back to the status message.
        }
        return null;
    }

    private static TimeSpan? ErrorLimitReset(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-ESI-Error-Limit-Reset", out var values) &&
        int.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static string Trim(string body) => body.Length <= 500 ? body : body[..500];
}
