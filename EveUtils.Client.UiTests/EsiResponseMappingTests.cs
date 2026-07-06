using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// "Play ESI" at the pivot: for each problem response ESI can hand back, assert the pivot maps it onto the right
/// <see cref="EsiErrorKind"/> so callers branch correctly (ESI-Reference §9). The retry handler is left out — these
/// are terminal mappings the caller sees, not retry decisions.
/// </summary>
public class EsiResponseMappingTests
{
    [Theory]
    [InlineData(403, "{\"error\":\"forbidden\",\"sso_status\":1}", EsiErrorKind.ScopeForbidden)]
    [InlineData(400, "{\"error\":\"bad request\"}", EsiErrorKind.BadRequest)]
    [InlineData(422, "{\"error\":\"unprocessable\"}", EsiErrorKind.BadRequest)]
    [InlineData(404, "{\"error\":\"not found\"}", EsiErrorKind.NotFound)]
    [InlineData(420, "{\"error\":\"error limited\"}", EsiErrorKind.RateLimited)]
    [InlineData(429, "{\"error\":\"throttled\"}", EsiErrorKind.RateLimited)]
    [InlineData(504, "{\"error\":\"gateway timeout\"}", EsiErrorKind.Timeout)]
    [InlineData(500, "{\"error\":\"boom\"}", EsiErrorKind.ServerError)]
    public async Task StatusCode_MapsToErrorKind(int status, string body, EsiErrorKind expected)
    {
        var result = await Pivot(new FixedResponseHandler((HttpStatusCode)status, body))
            .RequestAsync<object>(EsiRequest.Get("/characters/1/skills/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error!.Kind);
    }

    [Theory]
    [InlineData("<html>503 Service Unavailable</html>")] // an upstream proxy page, not JSON
    [InlineData("{ this is not valid json")]
    public async Task GarbageBodyOnSuccess_MapsToParseError(string body)
    {
        var result = await Pivot(new FixedResponseHandler(HttpStatusCode.OK, body))
            .RequestAsync<Probe>(EsiRequest.Get("/characters/1/skills/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.ParseError, result.Error!.Kind);
    }

    [Fact]
    public async Task ClientTimeout_MapsToTimeout()
    {
        var result = await Pivot(new ThrowingHandler(new TaskCanceledException()))
            .RequestAsync<object>(EsiRequest.Get("/characters/1/skills/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.Timeout, result.Error!.Kind);
    }

    [Fact]
    public async Task TransportFailure_MapsToNetwork()
    {
        var result = await Pivot(new ThrowingHandler(new HttpRequestException("connection refused")))
            .RequestAsync<object>(EsiRequest.Get("/characters/1/skills/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.Network, result.Error!.Kind);
    }

    private static IEsiClient Pivot(HttpMessageHandler transport) =>
        new EsiClient(
            new SingleClientFactory(new HttpClient(transport)),
            new UnusedTokenProvider(),
            new EsiOutageDetector(new EsiAvailabilityState()),
            NullLogger<EsiClient>.Instance);

    private sealed class Probe
    {
        public int Id { get; init; }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw toThrow;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class UnusedTokenProvider : IEsiTokenProvider
    {
        public Task<EsiAuthorization> AuthorizeAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A public call must not reach the token provider.");
    }
}
