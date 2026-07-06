using System.Net;
using EveUtils.Server;
using EveUtils.Server.Auth;
using EveUtils.Server.Components;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Server.Contracts;
using EveUtils.Server.Data;
using EveUtils.Server.Esi;
using EveUtils.Server.Fittings;
using EveUtils.Server.Grpc;
using EveUtils.Server.Messaging;
using EveUtils.Server.Permissions;
using EveUtils.Server.Stream;
using EveUtils.Server.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Logging;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.AdminAuth;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using EveUtils.Shared.Modules.AdminAuth.Services;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using EveUtils.Shared.Modules.Gamelog.Events;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.ServerAuth;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using EveUtils.Shared.Modules.Ships.Commands;
using EveUtils.Shared.Modules.Ships.Queries;
using EveUtils.Shared.Modules.Sync.Commands;
using EveUtils.Shared.Modules.Sync.Queries;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

// routing-seam headless proof: assert ConnectedClients targeted/broadcast routing and exit
// before starting the web host. Deterministic; needs no TLS/auth/DB.
if (args.Contains("--routing-test"))
    return await ConnectedClientsRoutingCheck.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// Static web assets (incl. _framework/blazor.web.js) are auto-loaded only in Development. The staging deploy
// runs `dotnet run` under ASPNETCORE_ENVIRONMENT=Staging from the build output, where that script lives only
// in the manifest (not physically in wwwroot) — so without this the Blazor bootstrap 404s and the admin panel
// never goes interactive. A published build materialises the files and this becomes a no-op.
builder.WebHost.UseStaticWebAssets();

// Self-signed TLS cert (generated on first start) — one HTTPS endpoint serving gRPC (HTTP/2) next to
// Blazor/SignalR (HTTP/1.1) via ALPN. The client pins the fingerprint during pairing (TOFU).
var httpsPort = builder.Configuration.GetValue("Server:HttpsPort", 7443);
// Data dir holds the DB, TLS cert, app-log, ESI cache and auth store. Anchored to the binary by default
// (so `dotnet run` and Rider share one DB, d3cfa5f). EVEUTILS_SERVER_DATA_DIR overrides it to an isolated
// throwaway location so the headless test suites never touch the real anchored DB (test isolation).
var dataDirectory = Environment.GetEnvironmentVariable("EVEUTILS_SERVER_DATA_DIR") is { Length: > 0 } dataDirOverride
    ? dataDirOverride
    : Path.Combine(AppContext.BaseDirectory, "data");
var certificate = new SelfSignedCertProvider(dataDirectory).GetOrCreate();
var certificateInfo = new ServerCertificateInfo(SelfSignedCertProvider.Fingerprint(certificate));

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpsPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps(certificate);
    });
});

builder.Services.AddSingleton(certificateInfo);
builder.Services.AddAppLogStore(dataDirectory: dataDirectory); // in-app error log (before other services)
builder.Services.AddGrpc();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(); // Blazor Server admin panel

// Admin-panel auth: cookie-auth (separate from ESI character-pairing) + authorization + a revalidating
// auth-state provider so a deactivated/demoted user is signed out. Only the Blazor panel pages are gated;
// the client↔server paths (gRPC/REST/SignalR/ESI pairing) stay open on their own auth.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, EveUtils.Server.Auth.RevalidatingAdminAuthStateProvider>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "EveUtilsPanelAuth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;       // server is HTTPS
        options.Cookie.SameSite = SameSiteMode.Strict;                 // panel has no cross-site need
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorization(options =>
{
    // One policy per panel permission code. A super-admin passes every policy; otherwise the user
    // must carry the matching permission claim. Built from the code list so a new code can't be forgotten.
    foreach (var code in PanelPermissions.All)
    {
        var required = code;
        options.AddPolicy(required, policy => policy.RequireAssertion(ctx => ctx.User.HasPanelPermission(required)));
    }
});
builder.Services.AddSignalR();                                          // DPS stream hub
builder.Services.AddHostedService<DpsBroadcastBridge>();                // server bus → SignalR bridge
builder.Services.AddHostedService<ServerTokenRefreshService>();         // token refresh
builder.Services.AddHostedService<ServerSessionCleanupService>();       // purge expired sessions
builder.Services.AddHostedService<MessageRetentionService>();           // purge expired queued messages

builder.Services.AddServerIdentity();                      // foundation: fixed server principal
// IPermissionToggleStore/EfPermissionToggleStore now live in Shared (Modules/Permissions/Repositories) and
// auto-register as a singleton via AddSharedServices (ISingletonService marker).
builder.Services.AddPermissionRegistry();                  // foundation: code-derived registry + OwnerAllPolicy (default)
builder.Services.AddSingleton<IAccessPolicy, ToggleablePolicy>(); // overrides OwnerAllPolicy for fit.sync (last-registered wins)
builder.Services.AddCqrs();                                // dispatcher behind the permission gate
builder.Services.AddEventBus();                            // local (in-process) event bus (+ remote-forward gate)
builder.Services.AddSharedServices();                      // central marker-scan over the shared assembly
builder.Services.AddAutoServices(typeof(Program).Assembly); // host-only marker-tagged services
builder.Services.AddServerDatabase(builder.Configuration, dataDirectory); // db (anchored to dataDirectory) + runtime + modules (handlers + permissions per module)

// ESI (Mode B confidential exchange) + server-auth (pairing, sessions, encrypted tokens, allowed-list).
var esiOptions = builder.Configuration.GetSection("Esi").Get<EsiOptions>() ?? new EsiOptions();
// Fail fast outside Development when the EVE SSO app is not configured: a server cannot run the Mode B
// confidential exchange without its own ClientId/ClientSecret. Register an application at
// developers.eveonline.com and supply the values via Esi__ClientId / Esi__ClientSecret (see
// docs/server-installation.md). Development falls back to appsettings.Development.json.
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(esiOptions.ClientId) || string.IsNullOrWhiteSpace(esiOptions.ClientSecret)))
{
    throw new InvalidOperationException(
        "ESI is not configured: set Esi__ClientId and Esi__ClientSecret. Register an EVE application at " +
        "https://developers.eveonline.com/ and see docs/server-installation.md.");
}
builder.Services.AddSingleton(esiOptions);
// The shared ESI HttpClient now comes from AddEsiPipeline's factory (header chain).
// ESI clients (IEsiAuthClient/IEsiJwtValidator/IEsiAffiliationResolver/IEsiRateLimitMonitor) are registered
// once by the central shared scan below (they carry ISingletonService) — no per-host AddSingleton.
builder.Services.AddModuleEsiScopes(new ServerOptionalScopeCatalog()); // optional server scopes
builder.Services.AddEsiScopeRegistry(); // built from all IEsiScopeCatalog registrations
builder.Services.AddEsiPipeline(dataDirectory); // pivot + handler chain + file cache (ServerEsiTokenProvider auto-registered)
builder.Services.AddHostedService(sp => sp.GetRequiredService<EsiCachePurgeService>()); // scheduled cache purge
builder.Services.AddHostedService(sp => sp.GetRequiredService<EveServerStatusService>()); // /status poll → drives the ESI downtime gate + outage detector server-side too
builder.Services.AddSingleton(new ServerInfo(builder.Configuration["Server:Name"] ?? "EVE Together Server"));
builder.Services.AddServerAuthModule(dataDirectory);
builder.Services.AddAdminAuthModule();      // admin users/roles/RBAC catalog for the Blazor panel
builder.Services.AddFittingsServerModule(); // SharedFit store + wire events + fit.sync gate
builder.Services.AddFleetModule();          // fleet persistence
builder.Services.AddMessagingModule();      // internal mail/invite queue wire catalog
builder.Services.AddSdeModule(dataDirectory); // read-only SDE store (types/dogma/slots) anchored to dataDirectory
builder.Services.AddHostedService<SdeImportHostedService>(); // autonomous, silent SDE updater on startup (no UI)
// ServerSessionService / FitSharedEventHandler / PairingCompleter / PairingStateStore carry lifetime
// markers and are picked up by AddAutoServices(host assembly) above.

// Remote event bus: wire-event registry + connected-clients presence.
builder.Services.AddWireEvents();
builder.Services.AddSingleton<ConnectedClients>();
builder.Services.AddHostedService<EventBusKeepaliveService>(); // liveness ping → clients detect a vanished server (tunnel half-open), ghosts get evicted
builder.Services.AddScoped<FleetBroadcastResolver>();       // Live broadcast set = roster members ∩ presence
builder.Services.AddScoped<FleetCleanupRunner>();           // one cleanup sweep (archive/hard-delete)
builder.Services.AddHostedService<FleetCleanupService>();   // periodic fleet cleanup

// Belt-and-suspenders on top of the bulletproof loops: a faulting BackgroundService must never tear down
// the host. The refresh loops already swallow per-cycle exceptions and keep running.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

var app = builder.Build();

// SDE end-to-end proof (real CCP download + build + accessor roundtrip). Deterministic; needs no TLS/auth.
if (args.Contains("--sde-import"))
    return await SdeImportCheck.RunAsync(app.Services);

// Fit text parsers (EFT + DNA) resolved against the real SDE.
if (args.Contains("--fit-parse-test"))
    return await FitParseCheck.RunAsync(app.Services);

// Dogma data accessor (attributes/effects/modifierInfo) against the real SDE.
if (args.Contains("--dogma-data-test"))
    return await DogmaDataCheck.RunAsync(app.Services);

// Dogma engine cross-check against a reference fit's stats.
if (args.Contains("--dogma-fit-test"))
    return await DogmaFitCheck.RunAsync(app.Services);

if (args.Contains("--dogma-eft"))
    return await DogmaEftCheck.RunAsync(app.Services, args);

app.UseStaticFiles();  // serves wwwroot (the DPS stream page)
app.UseAuthentication(); // admin-panel cookie auth
app.UseAuthorization();
app.UseAntiforgery();  // required by the interactive Blazor components

app.Logger.LogInformation("Server TLS cert fingerprint (pin this during pairing): {Fingerprint}", certificateInfo.Fingerprint);

// Subscribe to FitSharedEvent to persist incoming shared fits. The Func<TEvent,CancellationToken,Task>
// overload (not the Action one) so the handler is async-Task, not async-void — an unhandled exception in an
// async-void delegate would crash the process; here it is caught and logged best-effort instead.
_ = app.Services.GetRequiredService<IEventBus>().Subscribe<FitSharedEvent>(async (evt, _) =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<FitSharedEventHandler>();
        await handler.HandleAsync(evt);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Persisting an incoming shared fit failed.");
    }
});

// live-deliver a freshly enqueued message to its recipient. EnqueueMessageCommandHandler raises
// MessageEnqueuedEvent on every path (fleet start/conclude, invites, the responders), so live delivery is wired
// once here instead of being remembered at each gRPC call site. Best-effort — a delivery hiccup must not fail the
// enqueue; the durable queue + on-connect sweep are the offline fallback.
_ = app.Services.GetRequiredService<IEventBus>().Subscribe<MessageEnqueuedEvent>(async (evt, ct) =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessageDeliveryService>()
            .DeliverLiveAsync(evt.RecipientCharacterId, ct);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Live message delivery failed for character {Character}; the on-connect sweep will retry.",
            evt.RecipientCharacterId);
    }
});

app.MapGrpcService<PairingService>();
app.MapGrpcService<SessionService>();
app.MapGrpcService<EventBusStreamService>();
app.MapGrpcService<FittingsGrpcService>();
app.MapGrpcService<FleetsGrpcService>();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); // Blazor admin panel at "/"
app.MapHub<DpsHub>("/hubs/dps");                                // live DPS stream hub

// Admin-panel login: a non-interactive HTML form posts here; SignInAsync needs a writable HttpContext,
// so this is a minimal-API endpoint rather than a Blazor component event. Antiforgery is enforced by the
// UseAntiforgery middleware (the form carries an <AntiforgeryToken/>).
app.MapPost("/account/login", async (
    HttpContext http,
    [FromForm] string? username,
    [FromForm] string? password,
    IAdminAuthRepository repository,
    IAdminPasswordHasher hasher,
    CancellationToken ct) =>
{
    var normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
    var user = await repository.FindByNormalizedUsernameAsync(normalized, ct);
    if (user is null || !hasher.Verify(password ?? string.Empty, user.PasswordHash))
        return Results.Redirect("/login?error=1");
    if (!user.IsActive)
        return Results.Redirect("/login?error=inactive");

    await repository.SetLastLoginAsync(user.Id, DateTimeOffset.UtcNow, ct);

    var claims = await AdminClaims.BuildAsync(repository, user, ct);
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    // The forced-change flow (redirect to /account/password) is enforced globally; land on "/" here.
    return Results.Redirect("/");
});

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Change own password + clear the forced-change flag. A minimal-API endpoint so the cookie can
// be re-issued with fresh claims (MustChangePassword=false) — a Blazor component event has no writable HttpContext.
app.MapPost("/account/password", async (
    HttpContext http,
    [FromForm] string? current,
    [FromForm] string? newPassword,
    [FromForm] string? confirm,
    IAdminAuthRepository repository,
    IAdminPasswordHasher hasher,
    CancellationToken ct) =>
{
    if (!int.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        return Results.Redirect("/login");
    var user = await repository.GetUserAsync(userId, ct);
    if (user is null)
        return Results.Redirect("/login");

    if (!hasher.Verify(current ?? string.Empty, user.PasswordHash))
        return Results.Redirect("/account/password?error=current");
    if (!PasswordPolicy.IsValid(newPassword))
        return Results.Redirect("/account/password?error=length");
    if (!string.Equals(newPassword, confirm, StringComparison.Ordinal))
        return Results.Redirect("/account/password?error=confirm");
    if (string.Equals(newPassword, current, StringComparison.Ordinal))
        return Results.Redirect("/account/password?error=same");

    user.PasswordHash = hasher.Hash(newPassword!);
    user.MustChangePassword = false;
    await repository.UpdateUserAsync(user, ct);

    var claims = await AdminClaims.BuildAsync(repository, user, ct);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect("/");
}).RequireAuthorization();
app.MapGet("/stream/dps", () => Results.Redirect("/dps.html")); // the stream test page

// server scope declaration endpoint — queried by the client before Mode B pairing.
// Returns required + optional ESI scopes derived from the startup IEsiScopeRegistry.
// No auth required (public); returns 200 + JSON.
app.MapGet("/api/server/scopes", (IEsiScopeRegistry scopeReg, ServerInfo serverInfo) =>
{
    var reqs = scopeReg.GetRequirements(EsiScopeTarget.Server);
    var required = reqs.Where(r => r.Target == EsiScopeTarget.Both || r.Scope == "publicData")
                       .Select(r => r.Scope)
                       .Distinct()
                       .ToArray();
    var optional = reqs.Where(r => r.Target == EsiScopeTarget.Server && r.Scope != "publicData")
                       .Select(r => new ServerScopeInfo(r.Scope, r.Description, r.Feature))
                       .ToArray();
    return Results.Ok(new ServerScopesResponse(required, optional, serverInfo.Name)); // name for the couple dialog
});

// verification: log every live DPS sample that arrives over the remote bus from a client.
_ = app.Services.GetRequiredService<IEventBus>().Subscribe<CombatLoggedEvent>(evt =>
    app.Logger.LogInformation(
        "Remote DPS from {Character}: dealt={Dealt} received={Received}",
        evt.Data.CharacterName, evt.Data.DealtPerSecond, evt.Data.ReceivedPerSecond));

// Apply the migration stack (via the factory) and seed one sync log to demonstrate the server-only write.
using (var scope = app.Services.CreateScope())
{
    using (var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContext())
    {
        db.Database.Migrate();
    }

    // Backfill the fit content-hash for shared fits stored before the column existed, so they take part in dedup.
    await scope.ServiceProvider.GetRequiredService<ISharedFitRepository>().BackfillContentHashesAsync();

    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
    if ((await dispatcher.Query(new GetSyncLogsQuery())).Count == 0)
    {
        await dispatcher.Send(new AddSyncLogCommand("startup", DateTimeOffset.UtcNow, "server boot"));
    }

    // Seed the pairing allowed-list from config (Esi:AllowedCharacters) so the configured character can pair.
    var serverAuthRepository = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
    var allowedNames = app.Configuration.GetSection("Esi:AllowedCharacters").Get<string[]>() ?? [];
    await serverAuthRepository.EnsureAllowedSeedAsync(allowedNames);

    // Admin-panel auth seed: built-in roles + the bootstrap admin user (forced password change). Idempotent.
    // Outside Development a missing seed password is a fail-fast config error: never silently seed a known "admin"
    // password in Production, which would leave the panel open with admin/admin until the forced change.
    var adminSeedPassword = AdminSeedPassword.Resolve(
        app.Configuration["Server:AdminSeedPassword"], app.Environment.IsDevelopment());
    await AdminAuthSeeder.SeedAsync(
        scope.ServiceProvider.GetRequiredService<IAdminAuthRepository>(),
        scope.ServiceProvider.GetRequiredService<IAdminPasswordHasher>(),
        adminSeedPassword);

    // Development-only test seam: seed a
    // synthetic paired character + a session with a known token so the remote bus and the stream
    // page can be exercised end-to-end without a live EVE SSO. A Production deploy skips this.
    var devToken = app.Configuration["Server:DevTestToken"];
    if (app.Environment.IsDevelopment() && !string.IsNullOrEmpty(devToken))
    {
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();
        // Two synthetic characters + sessions so two client instances can connect at once for the fleet
        // two-client scenarios (invite round-trip, member graphs) without a live EVE SSO. The "-2" token is the
        // second instance's bearer (EVEUTILS_INSTANCE=B + --dev-couple <token> ...).
        var devCharacters = new[]
        {
            (Token: devToken, Name: "DevTester", Id: 91000000),
            (Token: devToken + "-2", Name: "DevTester2", Id: 91000001),
        };
        foreach (var dev in devCharacters)
        {
            var synced = await serverAuthRepository.UpsertSyncedAsync(dev.Id, dev.Name, protector.Protect("dev-refresh"));
            var now = DateTimeOffset.UtcNow;
            var existing = await serverAuthRepository.FindSessionByAccessHashAsync(TokenSecurity.Hash(dev.Token));
            // A previously seeded session can have expired across restarts. Re-seed a fresh session whenever
            // none exists or the existing one is expired — keeping the SAME access-hash (= the fixed devToken)
            // so the coupled dev client's bearer stays valid; never rotate the token.
            if (existing is null || existing.ExpiresAt <= now)
            {
                if (existing is not null)
                    await serverAuthRepository.DeleteSessionAsync(existing.Id);

                await serverAuthRepository.AddSessionAsync(new ServerSession
                {
                    SyncedCharacterId = synced.Id,
                    AccessTokenHash = TokenSecurity.Hash(dev.Token),
                    RefreshTokenHash = TokenSecurity.Hash(dev.Token + "-refresh"),
                    IssuedAt = now,
                    ExpiresAt = now.AddDays(1),
                    RefreshExpiresAt = now.AddDays(365), // else the cleanup (keyed on RefreshExpiresAt) purges the dev session
                    LastHeartbeat = now
                });
            }
        }
    }
}

// Admin-panel auth foundation headless proof: seeded roles/user, PBKDF2 hasher, panel registry,
// effective-permission resolution. Runs after the startup migration + seed.
if (args.Contains("--admin-auth-test"))
    return await AdminAuthCheck.RunAsync(app.Services);

// fleet-lifecycle headless proof: runs the create→edit→reject→disband scenario through the real
// DI container + dispatcher (also proves the Scrutor auto-registration resolves), then exits.
if (args.Contains("--fleet-test"))
    return await FleetLifecycleCheck.RunAsync(app.Services);

// fleet-structure headless proof: wings/squads CRUD + cascade-delete + creator-gate.
if (args.Contains("--fleet-structure-test"))
    return await FleetStructureCheck.RunAsync(app.Services);

// fleet-invite headless proof: invite round-trip, targeted delivery, accept/deny → roster.
if (args.Contains("--fleet-invite-test"))
    return await FleetInviteCheck.RunAsync(app.Services);

// fleet-discovery headless proof: open-fleet listing + direct join + connected-characters source.
if (args.Contains("--fleet-discovery-test"))
    return await FleetDiscoveryCheck.RunAsync(app.Services);

// participation headless proof: active-fleet gates + one-active rule + fleet-scoped routing.
if (args.Contains("--fleet-participation-test"))
    return await FleetParticipationCheck.RunAsync(app.Services);

if (args.Contains("--fleet-cleanup-test"))
    return await FleetCleanupCheck.RunAsync(app.Services);

if (args.Contains("--message-test"))
    return await MessageQueueCheck.RunAsync(app.Services);

// Fleet-roster headless proof: creator-as-member, wing/squad EVE-limits, member-move
// (ESI position rules, squad capacity, command-slot uniqueness, creator-gate).
if (args.Contains("--fleet-roster-test"))
    return await FleetRosterCheck.RunAsync(app.Services);

// Request-to-join headless proof: an invite-only fleet can't be joined directly; a
// character requests, the owner is messaged and accepts (→ roster) or declines, the requester is notified.
if (args.Contains("--fleet-join-request-test"))
    return await FleetJoinRequestCheck.RunAsync(app.Services);

// Invite-extension headless proof: an invite carries a free-text note (surfaced in the
// invitee's inbox), the inviter is notified on accept/decline, the roster lists a fleet's pending invites, and
// accepting an invite into a disbanded fleet fails cleanly instead of creating an orphaned member.
if (args.Contains("--fleet-invite-ext-test"))
    return await FleetInviteExtCheck.RunAsync(app.Services);

// External-member headless proof: the owner adds a session-less character directly on
// trust (no invite); it lands on the roster as an external SquadMember (-1/-1), with creator-gate, idempotency,
// archived-fleet and unknown-fleet rejection. The public-ESI lookup needs the network → it is out of scope here.
if (args.Contains("--fleet-external-test"))
    return await FleetExternalCheck.RunAsync(app.Services);

// Fleet-activation headless proof: a fleet forms, the creator Starts it (Forming → Active)
// and the roster (bar creator + externals) is notified; non-creator/archived Start is rejected; second Start is
// idempotent (no duplicate notification). Activation is independent of FleetState (the soft-delete lifecycle).
if (args.Contains("--fleet-activation-test"))
    return await FleetActivationCheck.RunAsync(app.Services);

// ownership-transfer + member-removal headless proof: a new fleet's default wing/squad,
// a joiner's auto-placement, creator-only ownership transfer (rejecting non-creator and non-member
// targets), member removal, and the creator-leave block before/after a transfer.
if (args.Contains("--fleet-ownership-test"))
    return await FleetOwnershipCheck.RunAsync(app.Services);

// One-active-fleet guard + Concluded lifecycle (2026-06-04): Conclude is creator-only/terminal and frees members,
// the entry-guard blocks a second active fleet but allows advance sign-up to a Forming one, and the broadcast
// tiebreak keeps a member coupled to the active fleet they were activated in first.
if (args.Contains("--fleet-active-guard-test"))
    return await FleetActiveGuardCheck.RunAsync(app.Services);

// Server shared-fit content-hash dedup (2026-06-04): re-sharing the same fit (different ESI id / owner / item order)
// matches the existing row instead of adding a duplicate, and reports which fit it matched; a different fit is added.
if (args.Contains("--fit-dedup-test"))
    return await FittingDedupCheck.RunAsync(app.Services);

// Auto-squad-on-join (2026-06-04): a joiner fills the default squad, then the next joiner auto-creates "Squad 2"
// in the same wing and lands there instead of dropping to the -1/-1 unassigned sentinel.
if (args.Contains("--fleet-autoplace-test"))
    return await FleetAutoPlaceCheck.RunAsync(app.Services);

var provider = app.Configuration["Database:Provider"];

app.MapGet("/status", () => Results.Ok(new
{
    role = "server",
    provider,
    message = "EVE Together server"
}));

// Shared module (Ships)
app.MapGet("/ships", (IDispatcher dispatcher, CancellationToken ct) =>
    dispatcher.Query(new GetShipsQuery(), ct));

app.MapPost("/ships", async (CreateShipRequest request, IDispatcher dispatcher, CancellationToken ct) =>
{
    var result = await dispatcher.Send(new AddShipCommand(request.Name, request.Class, request.Mass), ct);
    return result.IsSuccess
        ? Results.Created($"/ships/{result.Value}", new { id = result.Value })
        : Results.BadRequest(result.Messages);
});

// Server-only module (Sync)
app.MapGet("/sync-logs", (IDispatcher dispatcher, CancellationToken ct) =>
    dispatcher.Query(new GetSyncLogsQuery(), ct));

// Mode B SSO callback: EVE redirects the browser here (the server has its own ESI app + callback).
// The server completes the token exchange itself; the client just polls ClaimPairing.
app.MapGet("/auth/eve/callback", async (string? code, string? state, PairingStateStore store, PairingCompleter completer, ServerInfo serverInfo, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return Results.Content(PairingCallbackPage.Render("EVE Together — pairing", "<p>Missing code or state.</p>"), "text/html; charset=utf-8");

    var pairing = store.GetByState(state);
    if (pairing is null)
        return Results.Content(PairingCallbackPage.Render("EVE Together — pairing", "<p>Pairing not found or expired.</p>"), "text/html; charset=utf-8");

    var (ok, message) = await completer.CompleteAsync(pairing, code, state, ct);
    if (!ok)
        return Results.Content(PairingCallbackPage.Render("EVE Together — pairing failed", $"<p>{WebUtility.HtmlEncode(message)}</p>"), "text/html; charset=utf-8");

    // All values carry externally-sourced data (server name, character name, ESI corp/alliance), HTML-encoded inside
    // the page builder to avoid reflected XSS on the pairing-callback page.
    var affiliation = string.IsNullOrEmpty(pairing.AllianceName)
        ? pairing.CorporationName
        : $"{pairing.CorporationName} · {pairing.AllianceName}";
    return Results.Content(
        PairingCallbackPage.Success(serverInfo.Name, pairing.CharacterName, affiliation), "text/html; charset=utf-8");
});

app.Run();

return 0;

// Exposes the implicit top-level Program type so AddAutoServices(host assembly) and integration tests
// can reference it.
public partial class Program;
