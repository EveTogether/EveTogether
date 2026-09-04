using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace EveUtils.Server.Api;

/// <summary>
/// Everything the REST API needs under <c>ServerApi</c> in configuration. Every default is the closed one, so a
/// server nobody configured is the conservative server.
/// </summary>
public sealed class ServerApiOptions
{
    public const string Section = "ServerApi";

    /// <summary>
    /// Origins allowed to call the API from a browser. Empty — the ratified default — means no CORS headers at
    /// all, which is what server-to-server consumers and curl need. Filling it is the operator's decision.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Requests per minute allowed to one key — and, as one shared allowance, to everyone arriving
    /// without one. A second knob for the keyless bucket can come the day a legitimate caller ends up in it.</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>
    /// Addresses of proxies whose <c>X-Forwarded-*</c> headers may be believed — the tunnel connector's, in
    /// practice. Empty means the headers are ignored, because anyone may send them.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// How often an open realtime connection re-checks its key. There is no revocation event to wait for, so this
    /// is the longest a withdrawn key can keep a socket. A knob because a test has to shorten it, not because an
    /// operator is expected to move it.
    /// </summary>
    public TimeSpan RealtimeKeyRecheck { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// What the API needs because it is publicly reachable: a limit counted per key, a CORS valve that starts shut,
/// and forwarded headers honoured only from a proxy the operator named.
/// </summary>
public static class ServerApiHardening
{
    public const string CorsPolicy = "ServerApiCors";
    public const string RateLimitPolicy = "ServerApiPerKey";

    /// <summary>
    /// The category that writes the request line — query string and all — and <c>?apikey=</c> is a documented way
    /// in. Pinned here rather than left to appsettings so turning logging up cannot turn a key into a log line.
    /// </summary>
    private const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>
    /// The shared bucket for callers presenting nothing that parses as a key. Cannot collide with a real
    /// partition: a prefix is lowercase hex, and this is not.
    /// </summary>
    // ponytail: a caller sending well-formed but wrong keys still gets a bucket per prefix, and that path does
    // cost a database read — close it with a lookup-free check on the prefix's shape if it is ever abused.
    private const string Keyless = "keyless";

    public static ServerApiOptions AddServerApiHardening(this WebApplicationBuilder builder)
    {
        ServerApiOptions options =
            builder.Configuration.GetSection(ServerApiOptions.Section).Get<ServerApiOptions>() ?? new ServerApiOptions();

        builder.Logging.AddFilter(HostingDiagnostics, LogLevel.Warning);

        IServiceCollection services = builder.Services;
        services.AddSingleton(options); // the same instance the host binds, so the hub cannot read a second one
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (rejected, _) =>
            {
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                    rejected.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(RateLimitPolicy, context =>
                // On the key's public prefix, never on the address: the tunnel puts one address in front of every
                // consumer, so a limit per address is a limit on all of them at once. Everyone arriving without a
                // key shares one bucket instead — none of them is entitled to service, so sharing is the point,
                // and leaving them unlimited would make the keyless path the only unbounded way in.
                RateLimitPartition.GetFixedWindowLimiter(
                    ApiKeyAuthentication.PresentedPrefix(context.Request) ?? Keyless,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.RateLimitPerMinute,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        // Registered whatever the allowlist holds: with no origins the policy matches nothing and the middleware
        // writes no headers, so the ratified default is the shape of the code rather than a branch in it.
        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(options.AllowedOrigins)
            .WithHeaders(ApiKeyAuthentication.HeaderName)
            .WithMethods(HttpMethods.Get)));

        if (options.KnownProxies.Length > 0)
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // Cleared rather than added to: the default trusts loopback, and this list is the whole answer to
                // which machine may rename the client.
                forwarded.KnownProxies.Clear();
                foreach (string proxy in options.KnownProxies)
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            });

        return options;
    }

    /// <summary>
    /// Order is load-bearing. The real client address has to be in place before anything reads it, and CORS has to
    /// come before the limiter: a preflight carries no key by definition, so behind the limiter it would land in
    /// the keyless bucket and lock an allowlisted browser consumer out over traffic that is not its own.
    /// </summary>
    public static void UseServerApiHardening(this IApplicationBuilder app, ServerApiOptions options)
    {
        if (options.KnownProxies.Length > 0)
            app.UseForwardedHeaders();

        app.UseCors();
        app.UseRateLimiter();
    }
}
