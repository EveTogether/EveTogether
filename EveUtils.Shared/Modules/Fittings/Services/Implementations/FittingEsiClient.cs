using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EveUtils.Shared.App;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services;
using EveUtils.Shared.Runtime;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Fittings.Services.Implementations;

/// <summary>
/// ESI fittings client: nette ESI-burger with proper User-Agent,
/// <c>X-Compatibility-Date</c> pinned, cache-first, retry discipline,
/// and error-limit monitoring after every call.
/// </summary>
internal sealed class FittingEsiClient(
    HttpClient httpClient,
    IEsiRateLimitMonitor rateLimitMonitor,
    IRuntimeContext runtime,
    ILogger<FittingEsiClient> logger) : IFittingEsiClient, ISingletonService
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);

    // Simple per-character in-memory cache.
    private readonly ConcurrentDictionary<int, (IReadOnlyList<EsiFitting> Fittings, DateTimeOffset ExpiresAt)> _cache = new();

    // ── GET ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EsiFitting>> GetFittingsAsync(
        int characterId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        // Cache-first.
        if (_cache.TryGetValue(characterId, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
        {
            logger.LogInformation("Returning cached fittings for character {Id} (expires {At:HH:mm:ss} UTC).", characterId, cached.ExpiresAt);
            return cached.Fittings;
        }

        var url = $"{EsiEndpoints.PublicDataBaseUrl}/characters/{characterId}/fittings/";
        using var response = await SendWithRetryAsync(HttpMethod.Get, url, accessToken, body: null, cancellationToken);
        await RecordRateLimitAsync(response);

        await EnsureSuccessOrThrowAsync(response, "GET fittings", cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var fittings = JsonSerializer.Deserialize<List<EsiFitting>>(json)
                       ?? throw new InvalidOperationException("ESI returned null for fittings.");

        // Cache until ESI's Expires header (1-hour fallback).
        var expires = response.Content.Headers.Expires ?? DateTimeOffset.UtcNow.AddHours(1);
        _cache[characterId] = (fittings, expires);

        logger.LogInformation("Fetched {Count} fittings for character {Id} from ESI.", fittings.Count, characterId);
        return fittings;
    }

    // ── POST ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<int> PostFittingAsync(
        int characterId,
        string accessToken,
        EsiFittingWrite fitting,
        CancellationToken cancellationToken = default)
    {
        var url = $"{EsiEndpoints.PublicDataBaseUrl}/characters/{characterId}/fittings/";
        var json = JsonSerializer.Serialize(fitting);
        using var response = await SendWithRetryAsync(HttpMethod.Post, url, accessToken, json, cancellationToken);
        await RecordRateLimitAsync(response);

        await EnsureSuccessOrThrowAsync(response, "POST fitting", cancellationToken);
        _cache.TryRemove(characterId, out _); // invalidate cache

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var fittingId = doc.RootElement.GetProperty("fitting_id").GetInt32();

        logger.LogInformation("Posted fitting '{Name}' for character {Id} → fitting_id {FittingId}.", fitting.Name, characterId, fittingId);
        return fittingId;
    }

    // ── DELETE ────────────────────────────────────────────────────────────────────────────────────

    public async Task DeleteFittingAsync(
        int characterId,
        string accessToken,
        int fittingId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{EsiEndpoints.PublicDataBaseUrl}/characters/{characterId}/fittings/{fittingId}/";
        using var response = await SendWithRetryAsync(HttpMethod.Delete, url, accessToken, body: null, cancellationToken);
        await RecordRateLimitAsync(response);

        await EnsureSuccessOrThrowAsync(response, "DELETE fitting", cancellationToken);
        _cache.TryRemove(characterId, out _);

        logger.LogInformation("Deleted fitting {FittingId} for character {CharId}.", fittingId, characterId);
    }

    /// <summary>
    /// Throws with the ESI error body included so the real reason (not just "400 Bad Request")
    /// surfaces in the log window and the UI status.
    /// </summary>
    private async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"ESI {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}";
        logger.LogError("{Message}", message);
        throw new InvalidOperationException(message);
    }

    // ── HTTP with retry ────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method, string url, string accessToken, string? body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = BuildRequest(method, url, accessToken, body);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Network error on {Method} {Url} (attempt {Attempt}).", method, url, attempt + 1);
                if (attempt == MaxRetries) throw;
                await Backoff(attempt, cancellationToken);
                continue;
            }

            var status = (int)response.StatusCode;

            // Rate-limit headers recorded for every response.
            RecordRateLimitHeaders(response);

            // 4xx → no retry, except 420 Error Limited.
            if (status == 420)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                logger.LogError("ESI 420 Error Limited on {Url}. Waiting {Delay}s before stopping.", url, retryAfter.TotalSeconds);
                await Task.Delay(retryAfter, cancellationToken);
                return response; // propagate to caller to surface the error
            }

            if (status == 429)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (attempt < MaxRetries)
                {
                    logger.LogWarning("ESI 429 on {Url} (attempt {A}). Waiting {D}s.", url, attempt + 1, retryAfter.TotalSeconds);
                    response.Dispose();
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }
            }

            if (status is >= 400 and < 500)
                return response; // no retry

            // 5xx → retry with backoff.
            if (status >= 500 && attempt < MaxRetries)
            {
                logger.LogWarning("ESI {Status} on {Url} (attempt {A}/{Max}). Retrying.", status, url, attempt + 1, MaxRetries);
                response.Dispose();
                await Backoff(attempt, cancellationToken);
                continue;
            }

            return response;
        }

        throw new InvalidOperationException($"ESI call to {url} failed after {MaxRetries} retries.");
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string accessToken, string? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // Descriptive User-Agent with app name + contact info so CCP can reach us if a call misbehaves.
        // TryAddWithoutValidation because the User-Agent contains spaces/parens.
        request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent(runtime.Host));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return request;
    }

    private void RecordRateLimitHeaders(HttpResponseMessage response)
    {
        int? errorRemaining = null;
        DateTimeOffset? resetAt = null;

        if (response.Headers.TryGetValues("X-ESI-Error-Limit-Remain", out var remain) &&
            int.TryParse(remain.FirstOrDefault(), out var rem))
            errorRemaining = rem;

        if (response.Headers.TryGetValues("X-ESI-Error-Limit-Reset", out var reset) &&
            int.TryParse(reset.FirstOrDefault(), out var seconds))
            resetAt = DateTimeOffset.UtcNow.AddSeconds(seconds);

        rateLimitMonitor.Record(errorRemaining, resetAt);
    }

    private async Task RecordRateLimitAsync(HttpResponseMessage response)
    {
        RecordRateLimitHeaders(response);
        await Task.CompletedTask;
    }

    private static Task Backoff(int attempt, CancellationToken cancellationToken)
    {
        var delay = RetryBaseDelay * Math.Pow(2, attempt);
        return Task.Delay(delay, cancellationToken);
    }
}
