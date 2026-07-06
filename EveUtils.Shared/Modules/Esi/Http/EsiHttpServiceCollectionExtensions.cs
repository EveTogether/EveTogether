using EveUtils.Shared.Modules.Esi.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Registers the shared ESI HTTP layer: the central <see cref="IEsiClient"/> pivot, the named
/// <see cref="IHttpClientFactory"/> clients, the <c>DelegatingHandler</c> chain (header → cache →
/// rate-limit → retry, Deel 2-6) and the file cache + purge service. Both composition roots call
/// this once; the host-specific <see cref="IEsiTokenProvider"/> is auto-registered from each host assembly
/// . The background services it registers (<see cref="EsiCachePurgeService"/>, the
/// <see cref="EveServerStatusService"/> /status poller) are singletons here; each host starts them — the server via
/// <c>AddHostedService</c>, the client manually — so the downtime gate is driven on both.
/// </summary>
public static class EsiHttpServiceCollectionExtensions
{
    public static IServiceCollection AddEsiPipeline(this IServiceCollection services, string cacheDirectory)
    {
        services.AddSingleton(EsiRetryPolicy.Default);
        services.AddSingleton<IEsiCacheStore>(new FileEsiCacheStore(Path.Combine(cacheDirectory, "esi-cache")));
        services.AddSingleton<EsiCachePurgeService>();
        services.AddSingleton<EveServerStatusService>(); // /status poller drives availability on both hosts (the server hosts it so its gate engages during downtime; the client also uses it for the status bar)

        services.TryAddSingleton(TimeProvider.System); // injectable clock for the downtime gate (testable)
        services.AddTransient<EsiHeaderHandler>();
        services.AddTransient<EsiCacheHandler>();
        services.AddTransient<EsiGatingHandler>();
        services.AddTransient<EsiRateLimitHandler>();
        services.AddTransient<EsiRetryHandler>();

        // The data client carries the full chain (outer → inner): a cache hit short-circuits before the
        // rate-limit + retry handlers so it costs no socket call and no rate-limit budget (Deel 2). The
        // gating handler sits just inside the cache, so a fresh cache hit is still served during downtime
        // while a call that would hit the network is withheld.
        services.AddHttpClient(EsiHttpClients.Data)
            .AddHttpMessageHandler<EsiHeaderHandler>()
            .AddHttpMessageHandler<EsiCacheHandler>()
            .AddHttpMessageHandler<EsiGatingHandler>()
            .AddHttpMessageHandler<EsiRateLimitHandler>()
            .AddHttpMessageHandler<EsiRetryHandler>();

        // The SSO token endpoint and the legacy clients are bare — only the header handler (no
        // cache/rate-limit/retry). The legacy clients keep their own behaviour; only the pivot carries the
        // full chain (full migration to the pivot is a follow-up).
        services.AddHttpClient(EsiHttpClients.Auth)
            .AddHttpMessageHandler<EsiHeaderHandler>();
        services.AddHttpClient(EsiHttpClients.Legacy)
            .AddHttpMessageHandler<EsiHeaderHandler>();

        services.AddSingleton<IEsiClient, EsiClient>();

        // Legacy ESI clients (affiliation/public-info/fittings) inject a plain HttpClient; hand them the
        // bare legacy client so they get central User-Agent + compatibility-date without the chain. A
        // factory client kept as a singleton is acceptable for this local-first app (no rotation pressure).
        services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(EsiHttpClients.Legacy));

        return services;
    }
}
