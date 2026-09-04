using System.Collections.Concurrent;
using System.Net;
using EveUtils.Server.Api;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-141: what the API needs because it is publicly reachable. Driven over the wire throughout — the limit, the
/// 401 and the CORS headers are all produced by the pipeline, so anything asserted below the socket would prove
/// the wrong thing.
/// </summary>
public class ServerApiHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Needs a key and nothing else, so a limit or a header is the only thing under test.</summary>
    private const string WhoAmI = "/api/v1/whoami";

    // --- The limit ---

    /// <summary>
    /// The one that decides whether this milestone works at all. Both keys come from the same address, because
    /// behind the tunnel every key does: if the partition were the address, exhausting one key would take the
    /// other down with it and the second assertion would fail instead of the third.
    /// </summary>
    [Fact]
    public async Task TheLimit_CountsPerKey_SoOneKeyOverItLeavesAnotherFromTheSameAddressAlone()
    {
        await using Host host = await _StartAsync(new() { ["ServerApi:RateLimitPerMinute"] = "2" });
        string first = await host.MintKeyAsync();
        string second = await host.MintKeyAsync();

        HttpStatusCode[] firstKey =
        [
            await host.StatusAsync(WhoAmI, first),
            await host.StatusAsync(WhoAmI, first),
            await host.StatusAsync(WhoAmI, first)
        ];

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], firstKey);
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(WhoAmI, second));

        // Ratified exception 4: /health stays reachable whatever a key is doing, so it carries no limit at all.
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync("/api/v1/health", first));
    }

    /// <summary>
    /// The keyless path is the one anybody can take, so it may not be the one without a limit. All of them share
    /// a single bucket — and the two things that must stay out of it are asserted from inside an exhausted one,
    /// because that is the only state in which their exemption is worth anything.
    /// </summary>
    [Fact]
    public async Task CallersWithoutAKey_ShareOneBucket_ThatCatchesNeitherHealthNorAPreflight()
    {
        await using Host host = await _StartAsync(new()
        {
            ["ServerApi:RateLimitPerMinute"] = "2",
            ["ServerApi:AllowedOrigins:0"] = "https://widgets.example"
        });

        // Two different callers with nothing to identify them, so the third refusal can only come from a bucket
        // they share — a bucket each, or none at all, would leave every one of these at 401.
        HttpStatusCode[] keyless =
        [
            await host.StatusAsync(WhoAmI, key: null),
            await host.StatusAsync(WhoAmI, key: "not-a-key"),
            await host.StatusAsync(WhoAmI, key: null)
        ];

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], keyless);

        // Ratified exception 4: /health is public and keyless, so it cannot be collateral of the keyless bucket.
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync("/api/v1/health", key: null));

        // A preflight carries no key by definition. If it fell in the bucket, an allowlisted browser consumer
        // would be locked out by traffic that has nothing to do with it.
        var preflight = new HttpRequestMessage(HttpMethod.Options, WhoAmI);
        preflight.Headers.Add("Origin", "https://widgets.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using HttpResponseMessage answered = await host.Client.SendAsync(preflight, Ct);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        Assert.Contains("Access-Control-Allow-Origin", answered.Headers.Select(h => h.Key));
    }

    // --- Expiry ---

    /// <summary>
    /// A key that has run out is refused, and says so where a client looks — without the key itself turning up
    /// anywhere in the answer, headers included.
    /// </summary>
    [Fact]
    public async Task AnExpiredKey_IsRefusedWithATraceableReason_AndTheAnswerDoesNotContainTheKey()
    {
        await using Host host = await _StartAsync([]);
        string expired = await host.MintKeyAsync(expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        using HttpResponseMessage response = await host.GetAsync(WhoAmI, expired);
        string answer = response.Headers + response.Content.Headers.ToString() +
                        await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("expired", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expired, answer, StringComparison.Ordinal);
    }

    // --- The key in the logs ---

    /// <summary>
    /// The second boundary where it matters which case broke, hence both ways in: the header is the documented
    /// default, and the query string is the one that leaks, because the framework's own request log writes it.
    /// The audit line is asserted in the same pass — it has to name the key without being it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheKeyNeverReachesALogLine_HoweverItArrives_ThoughTheAuditLineNamesItsPrefix(bool viaQuery)
    {
        // Logging wide open, the way an operator chasing a problem sets it. Shipped appsettings quiets the
        // framework's request log, and that is a setting anyone may undo — so the guard cannot live there.
        await using Host host = await _StartAsync(new()
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace"
        });
        string key = await host.MintKeyAsync();
        ApiKeySecurity.TryParse(key, out string prefix, out string secret);

        using HttpResponseMessage response = viaQuery
            ? await host.GetAsync($"{WhoAmI}?{ApiKeyAuthentication.QueryName}={key}", key: null)
            : await host.GetAsync(WhoAmI, key);
        response.EnsureSuccessStatusCode();

        string[] lines = [.. host.Logged];
        // Proof the request log is running at all: without it the two assertions below pass on an empty room.
        Assert.Contains(lines, line => line.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(key, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(prefix, StringComparison.Ordinal) && line.Contains(WhoAmI, StringComparison.Ordinal));
    }

    // --- CORS ---

    /// <summary>
    /// The valve: shut unless the operator opens it. The empty case is the ratified default, the filled one is
    /// the documented way in — a valve that cannot open would make the documentation a lie.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("https://widgets.example")]
    public async Task CorsHeadersAppear_OnlyForAnOriginTheOperatorAllowlisted(string? allowed)
    {
        await using Host host = await _StartAsync(allowed is null
            ? []
            : new Dictionary<string, string?> { ["ServerApi:AllowedOrigins:0"] = allowed });
        string key = await host.MintKeyAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, WhoAmI);
        request.Headers.Add(ApiKeyAuthentication.HeaderName, key);
        request.Headers.Add("Origin", "https://widgets.example");
        using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(allowed is not null, response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // --- Host ---

    private static async Task<Host> _StartAsync(Dictionary<string, string?> settings)
    {
        var factory = new SqliteServerDbContextFactory();
        var keys = new ApiKeyRepository(factory);
        var captured = new CapturingLoggerProvider();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(captured);

        builder.Services.AddSingleton<IApiKeyRepository>(keys);
        builder.Services.AddScoped<ServerApiQueries>();
        builder.Services.AddAuthentication(ApiKeyAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.Scheme, null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(ApiKeyAuthentication.Policy, ApiKeyAuthentication.BuildPolicy()));
        builder.Services.AddServerApiDocs();
        // Read from configuration rather than handed an object: the allowlist and the limit are settings, so the
        // binding is part of what has to work.
        ServerApiOptions hardening = builder.AddServerApiHardening();

        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseServerApiHardening(hardening);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapServerApi();

        await app.StartAsync(Ct);
        return new Host(app, factory, keys, captured);
    }

    private sealed class Host(
        WebApplication app,
        SqliteServerDbContextFactory factory,
        IApiKeyRepository keys,
        CapturingLoggerProvider captured) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

        public IReadOnlyCollection<string> Logged => captured.Lines;

        /// <summary>A key with no owner: admin scope over all server data (ratified decision 3).</summary>
        public async Task<string> MintKeyAsync(DateTimeOffset? expiresAt = null)
        {
            GeneratedApiKey generated = ApiKeySecurity.Generate();
            await keys.AddAsync(new ApiKey
            {
                Label = "tests",
                Prefix = generated.Prefix,
                SecretHash = ApiKeySecurity.Hash(generated.Secret),
                Scopes = ApiKeyScopes.ReadAll,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                CreatedBy = "tests"
            }, Ct);
            return generated.PlainText;
        }

        public Task<HttpResponseMessage> GetAsync(string path, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (key is not null)
                request.Headers.Add(ApiKeyAuthentication.HeaderName, key);
            return Client.SendAsync(request, Ct);
        }

        public async Task<HttpStatusCode> StatusAsync(string path, string? key)
        {
            using HttpResponseMessage response = await GetAsync(path, key);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            factory.Dispose();
        }
    }

    /// <summary>Every line the host wrote, formatted as a provider would write it out.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);

        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
