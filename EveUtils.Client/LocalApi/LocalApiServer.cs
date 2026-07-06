using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.App;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// In-process Kestrel host for the opt-in local widget API. Binds the
/// loopback interface only (127.0.0.1) — never an external interface — and serves <c>GET /api/v1/health</c>.
/// Lifecycle is driven by the persisted <see cref="EnabledSettingKey"/>/<see cref="PortSettingKey"/> settings.
/// The host's own DI container is separate; the captured <see cref="_rootServices"/> is the seam later
/// milestones use to read the existing client singletons (fits/fleet/metrics) without a second data layer.
/// </summary>
public sealed class LocalApiServer(
    ISettingRepository settings,
    IServiceProvider rootServices,
    ILogger<LocalApiServer> logger) : ILocalApiServer
{
    public const string EnabledSettingKey = "localapi.enabled";
    public const string PortSettingKey = "localapi.port";
    public const string ApiKeySettingKey = "localapi.apikey";
    public const string AllowedOriginsSettingKey = "localapi.allowedorigins"; // comma-separated; empty = allow any (default)
    public const int DefaultPort = 8001;
    public const string ApiVersion = "v1";

    private readonly IServiceProvider _rootServices = rootServices;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private LocalApiBroadcaster? _broadcaster;

    public LocalApiStatusSnapshot Status { get; private set; } = LocalApiStatusSnapshot.Stopped(DefaultPort);

    public event Action<LocalApiStatusSnapshot>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var (enabled, port) = await _ReadConfigAsync(cancellationToken);
        await ApplyAsync(enabled, port, cancellationToken);
    }

    public async Task ApplyAsync(bool enabled, int port, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _StopHostAsync();
            if (!enabled)
            {
                _SetStatus(LocalApiStatusSnapshot.Stopped(port));
                return;
            }

            await _StartHostAsync(port, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var port = Status.Port;
            await _StopHostAsync();
            _SetStatus(LocalApiStatusSnapshot.Stopped(port));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _StartHostAsync(int port, CancellationToken cancellationToken)
    {
        var all = await settings.ListAsync(cancellationToken);
        var apiKey = all.FirstOrDefault(s => s.Key == ApiKeySettingKey)?.Value; // optional shared-secret gate
        var allowedOrigins = (all.FirstOrDefault(s => s.Key == AllowedOriginsSettingKey)?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // the host stays quiet; this service logs its own lifecycle
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port)); // 127.0.0.1 only
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddOpenApi(); // self-documenting: /openapi/v1.json + Scalar UI below
        builder.Services.AddSingleton(new LocalApiQueries(_rootServices)); // reads the existing client services
        // Read-only loopback game data → CORS open by default for browser/OBS widgets; a configured allowlist
        // (localapi.allowedorigins) locks it down for the cautious user.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyHeader().AllowAnyMethod();
            if (allowedOrigins.Length > 0) policy.WithOrigins(allowedOrigins);
            else policy.AllowAnyOrigin();
        }));

        var broadcaster = new LocalApiBroadcaster(_rootServices, logger); // shared realtime fan-out (WS + SignalR)
        builder.Services.AddSingleton(broadcaster);                       // so FleetHub can report connect/disconnect
        builder.Services.AddSignalR()
            .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        app.UseWebSockets(); // enables the /ws realtime endpoint
        broadcaster.AttachHub(app.Services.GetRequiredService<IHubContext<FleetHub>>()); // hub fan-out
        app.UseCors();
        _UseSecurityGuards(app, apiKey); // host-header (anti-rebinding) + optional API key

        var baseUrl = $"http://127.0.0.1:{port}";
        app.MapGet("/", () => Results.Content(LocalApiDocs.Render(LocalApiDocs.IndexResource, baseUrl), "text/html"))
            .ExcludeFromDescription();
        app.MapGet("/docs", () => Results.Content(LocalApiDocs.Render(LocalApiDocs.DocsResource, baseUrl), "text/html"))
            .ExcludeFromDescription();
        app.MapGet("/widget", () => Results.Content(LocalApiDocs.Render(LocalApiDocs.WidgetResource, baseUrl), "text/html"))
            .ExcludeFromDescription();

        app.MapGet("/api/v1/health", () => new HealthResponse("ok", _AppVersion(), ApiVersion));
        app.MapGet("/api/v1/metrics", (LocalApiQueries queries, CancellationToken ct) => queries.GetMetricsAsync(ct));
        app.MapGet("/api/v1/characters", (LocalApiQueries queries, CancellationToken ct) => queries.GetCharactersAsync(ct));
        app.MapGet("/api/v1/fits", (LocalApiQueries queries, CancellationToken ct) => queries.GetFitsAsync(ct));
        app.MapGet("/api/v1/fits/{id:int}", async (int id, bool? stats, string? server, LocalApiQueries queries, CancellationToken ct) =>
            await queries.GetFitAsync(id, server, stats ?? false, ct) is { } fit ? Results.Ok(fit) : Results.NotFound());

        app.MapGet("/api/v1/fleets", (LocalApiQueries queries, CancellationToken ct) => queries.GetFleetsAsync(ct));
        app.MapGet("/api/v1/fleet", async (LocalApiQueries queries, CancellationToken ct) =>
            await queries.GetActiveFleetAsync(ct) is { } fleet ? Results.Ok(fleet) : Results.NoContent()); // 204 = not participating
        app.MapGet("/api/v1/compositions", (LocalApiQueries queries, CancellationToken ct) => queries.GetCompositionsAsync(ct));
        app.MapGet("/api/v1/compositions/{id:long}", async (long id, string? server, LocalApiQueries queries, CancellationToken ct) =>
            await queries.GetCompositionAsync(id, server, ct) is { } composition ? Results.Ok(composition) : Results.NotFound());
        app.MapGet("/api/v1/types/{id:int}", (int id, LocalApiQueries queries) => queries.GetTypeInfo(id)); // name/icon resolver

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await broadcaster.HandleAsync(socket, context.RequestAborted);
        });
        app.MapHub<FleetHub>("/hub/fleet"); // SignalR mirror of the /ws stream

        app.MapOpenApi();              // GET /openapi/v1.json
        app.MapScalarApiReference();   // interactive reference at /scalar

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex) // Kestrel wraps "address already in use" as IOException
        {
            await app.DisposeAsync();
            logger.LogWarning(ex, "Local API could not bind port {Port} (in use).", port);
            _SetStatus(new LocalApiStatusSnapshot(LocalApiStatus.PortInUse, port,
                Message: $"Port {port} is already in use."));
            return;
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            logger.LogError(ex, "Local API failed to start on port {Port}.", port);
            _SetStatus(new LocalApiStatusSnapshot(LocalApiStatus.Error, port, Message: ex.Message));
            return;
        }

        _app = app;
        broadcaster.Start(); // fleet-event subscriptions + 1 Hz own-metrics tick, now the host is up
        _broadcaster = broadcaster;
        var bound = _BoundAddresses(app);
        logger.LogInformation("Local API listening on {Url}.", $"http://127.0.0.1:{port}");
        _SetStatus(new LocalApiStatusSnapshot(LocalApiStatus.Running, port,
            Url: $"http://127.0.0.1:{port}", BoundAddresses: bound));
    }

    private async Task _StopHostAsync()
    {
        if (_broadcaster is { } broadcaster)
        {
            _broadcaster = null;
            await broadcaster.StopAsync(); // close sockets + drop subscriptions before the host
        }

        if (_app is null) return;
        var app = _app;
        _app = null;
        try
        {
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local API host did not stop cleanly.");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private async Task<(bool Enabled, int Port)> _ReadConfigAsync(CancellationToken cancellationToken)
    {
        var all = await settings.ListAsync(cancellationToken);
        var enabled = all.FirstOrDefault(s => s.Key == EnabledSettingKey)?.Value == "true"; // default off
        var port = int.TryParse(all.FirstOrDefault(s => s.Key == PortSettingKey)?.Value, out var p) && _IsValidPort(p)
            ? p
            : DefaultPort;
        return (enabled, port);
    }

    /// <summary>Anti-DNS-rebinding host-header guard, plus the optional shared-secret gate on /api, /ws and /hub.</summary>
    private static void _UseSecurityGuards(WebApplication app, string? apiKey)
    {
        app.Use(async (context, next) =>
        {
            // Only honour requests actually addressed to the loopback host: a malicious page that resolves a name to
            // 127.0.0.1 still carries its own Host header, so this rejects the DNS-rebinding attack.
            var host = context.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            await next();
        });

        if (string.IsNullOrEmpty(apiKey))
            return;

        var expected = Encoding.UTF8.GetBytes(apiKey);
        app.Use(async (context, next) =>
        {
            if (_NeedsKey(context.Request.Path) && !_KeyMatches(context.Request, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });
    }

    private static bool _NeedsKey(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/ws") || path.StartsWithSegments("/hub");

    private static bool _KeyMatches(HttpRequest request, byte[] expected)
    {
        var provided = request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(provided)) provided = request.Query["key"].ToString();
        return !string.IsNullOrEmpty(provided)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expected);
    }

    private static bool _IsValidPort(int port) => port is > 0 and <= 65535;

    private static IReadOnlyList<string> _BoundAddresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList()
        ?? new List<string>();

    private static string _AppVersion() => AppInfo.Version;

    private void _SetStatus(LocalApiStatusSnapshot snapshot)
    {
        Status = snapshot;
        StatusChanged?.Invoke(snapshot);
    }
}
