using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace EveUtils.Client.Esi.Testing;

/// <summary>Builders for scripted ESI responses used by the <c>--esi-test</c> scenarios.</summary>
public static class EsiTestResponses
{
    public static HttpResponseMessage Json(int status, string body)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return response;
    }

    public static HttpResponseMessage WithExpires(this HttpResponseMessage response, TimeSpan fromNow)
    {
        response.Content.Headers.Expires = DateTimeOffset.UtcNow.Add(fromNow);
        return response;
    }

    public static HttpResponseMessage WithETag(this HttpResponseMessage response, string tag, bool weak = false)
    {
        response.Headers.ETag = new EntityTagHeaderValue($"\"{tag}\"", weak);
        return response;
    }

    public static HttpResponseMessage WithErrorLimit(this HttpResponseMessage response, int remain, int resetSeconds)
    {
        response.Headers.TryAddWithoutValidation("X-ESI-Error-Limit-Remain", remain.ToString());
        response.Headers.TryAddWithoutValidation("X-ESI-Error-Limit-Reset", resetSeconds.ToString());
        return response;
    }

    public static HttpResponseMessage WithBucket(this HttpResponseMessage response, string group, int limit, int remaining, int used)
    {
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Group", group);
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Limit", $"{limit}/15m");
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Remaining", remaining.ToString());
        response.Headers.TryAddWithoutValidation("X-Ratelimit-Used", used.ToString());
        return response;
    }

    public static HttpResponseMessage WithRetryAfter(this HttpResponseMessage response, int seconds)
    {
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
