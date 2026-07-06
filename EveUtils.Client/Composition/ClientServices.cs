using System;
using System.IO;
using System.Net.Http;
using EveUtils.Client.Data;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Client.Pairing;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Shared.App;
using EveUtils.Shared.Data;
using EveUtils.Shared.Runtime;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Logging;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Client.Skills;
using EveUtils.Client.Implants;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Repositories;
using EveUtils.Shared.Modules.Implants;
using EveUtils.Shared.Modules.Implants.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>Composition root of the desktop client.</summary>
public static class ClientServices
{
    // Last-resort fallback for the bundled "our" app id if appsettings.json is missing. The real
    // defaults live in appsettings.json (committed); override via appsettings.Development.json or the
    // standard env convention (Esi__ClientId / Esi__ClientSecret). The client_secret is left out of
    // committed config + tooling (Iron Law #8) — paste it into appsettings.Development.json or env.
    // Warning: a secret bundled in an open-source client is extractable from the binary — bundle it knowingly.
    private const string FallbackEsiClientId = "5f1b2bd8bdb24153bdc42e3b8718f8a0";

    /// <param name="configure">Optional last-step hook over the service collection, applied just before the provider
    /// is built. Used by headless UI tests to override a registration (e.g. a fake <c>IFleetTransportClient</c> or
    /// <c>IDialogService</c>) — last registration wins. Production callers pass nothing.</param>
    public static IServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();

        services.AddAppLogStore(dataDirectory: DataDirectory()); // in-app error log
        services.AddLocalIdentity();     // foundation: the single local owner (principal)
        services.AddPermissionRegistry(); // foundation: code-derived registry + OwnerAllPolicy
        services.AddCqrs();              // dispatcher behind the permission gate
        services.AddEventBus();          // local (in-process) event bus (+ remote-forward gate)
        services.AddSharedServices();    // central marker-scan over the shared assembly
        services.AddAutoServices(typeof(ClientServices).Assembly); // host-only marker-tagged services
        services.AddSingleton<IWireEventCatalog, FleetWireEvents>(); // deserialize fleet invite events aimed at us
        services.AddSingleton<IWireEventCatalog, MessagingWireEvents>(); // deserialize message deliveries aimed at us
        services.AddWireEvents();        // event-type registry for the remote bus
        services.AddClientDatabase(ClientDbConnectionString()); // per-instance SQLite (EVEUTILS_INSTANCE)
        services.AddSdeModule(DataDirectory()); // read-only SDE store (user-prompted import + progress popup)
        services.AddHttpClient(TypeImageProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://images.evetech.net/");
            // Nice CCP citizen: identify the app on the image server too, though it is not ESI itself.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent(ExecutionHost.Client));
        });
        services.AddSingleton<ITypeImageProvider>(sp => new TypeImageProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ISettingRepository>(),
            DataDirectory())); // opt-in CCP type images, per-instance disk cache
        services.AddSingleton<ICharacterPortraitProvider>(sp => new CharacterPortraitProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ISettingRepository>(),
            DataDirectory())); // hex character portraits in the shell, per-instance disk cache
        // Market prices, character skills, training queue + attributes and
        // implants repositories live in Shared and auto-register via AddSharedServices.
        services.AddSingleton<IEsiSkillImporter>(sp =>
            new EsiSkillImporter(sp.GetRequiredService<IEsiClient>(), sp.GetRequiredService<ICharacterSkillRepository>(),
                sp.GetRequiredService<ICharacterSkillQueueRepository>(), sp.GetRequiredService<ICharacterAttributesRepository>())); // skills + queue/attributes import
        services.AddSingleton<IEsiImplantImporter>(sp =>
            new EsiImplantImporter(sp.GetRequiredService<IEsiClient>(), sp.GetRequiredService<ICharacterImplantRepository>())); // implants import
        services.AddSingleton(TimeProvider.System); // injectable clock
        services.AddSingleton<IThemeService, ThemeService>(); // runtime faction theming (live swap + persistence)
        // Public corp/alliance lookups go through ICharacterInfoService + the shared metered IEsiAffiliationResolver.
        // EsiExternalCharacterSource + ExternalCharacterLookup carry lifetime markers → auto-registered.
        // IExternalCharacterCache/EfExternalCharacterCache now live in Shared (Modules/Fleet/Repositories) and
        // auto-register as a singleton via AddSharedServices (ISingletonService marker).
        // GamelogClientService (also the IFleetMetricSource), GamelogWatcherService, SyntheticDpsFeeder,
        // ActiveFleetState and FleetMetricPublisher carry lifetime markers → auto-registered above.

        AddEsi(services, configuration);
        // Transport (gRPC channel/clients + the session/registry/trust/inbox stores) carries lifetime
        // markers and auto-registers via AddSharedServices + AddAutoServices(client assembly) — no wiring here.

        // Opt-in local widget API host (loopback only, default off) — started manually in Program.Main like
        // the other background services. Captures the root provider as the seam for later milestones to read
        // the existing client singletons (fits/fleet/metrics).
        services.AddSingleton<LocalApi.ILocalApiServer, LocalApi.LocalApiServer>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void AddEsi(IServiceCollection services, IConfiguration configuration)
    {
        var esi = configuration.GetSection("Esi").Get<EsiOptions>() ?? new EsiOptions();
        var options = new EsiOptions
        {
            ClientId = string.IsNullOrEmpty(esi.ClientId) ? FallbackEsiClientId : esi.ClientId,
            ClientSecret = string.IsNullOrEmpty(esi.ClientSecret) ? null : esi.ClientSecret,
            CallbackUri = string.IsNullOrEmpty(esi.CallbackUri) ? "http://127.0.0.1:7345/callback" : esi.CallbackUri,
            Scopes = esi.Scopes
        };
        services.AddSingleton(options);

        // The shared ESI HttpClient now comes from AddEsiPipeline's factory (header chain).
        // IEsiAuthClient/IEsiJwtValidator/IEsiRateLimitMonitor are registered once by AddSharedServices()
        // (they carry ISingletonService) — no per-host AddSingleton.
        services.AddSingleton<IPerCharacterTokenStore>(_ => new EncryptedPerCharacterTokenStore(DataDirectory()));
        services.AddSingleton<ICharacterRegistry>(sp =>
            new EfCharacterRegistry(sp.GetRequiredService<IDbContextFactory<SharedDbContext>>())); // SQLite-backed
        services.AddFittingsModule();    // fittings import/push + ESI client + scope catalog
        services.AddModuleEsiScopes(SkillsScopeCatalog.Catalog); // esi-skills read_skills + read_skillqueue
        services.AddModuleEsiScopes(ImplantsScopeCatalog.Catalog); // esi-clones read_implants
        services.AddModuleEsiScopes(FleetsScopeCatalog.Catalog); // esi-fleets read/write (opt-in, Q1) for in-game fleet coupling
        services.AddEsiScopeRegistry(); // built from all IEsiScopeCatalog registrations (modules)
        services.AddEsiPipeline(DataDirectory()); // pivot + handler chain + file cache (ClientEsiTokenProvider auto-registered)
        // BackgroundService started manually (the client has no generic host); the other ESI services
        // (LocalEsiLoginService, DialogService) carry markers and are auto-registered above.
        services.AddSingleton<ClientTokenRefreshService>();
        // EveServerStatusService is registered by AddEsiPipeline (shared) now that the server hosts it too.
        services.AddSingleton<EsiMarketPriceService>();  // hourly public /markets/prices/ refresh into the cache
        services.AddSingleton<ICharacterInfoService, CharacterInfoService>(); // public char+corp affiliation via the metered pipeline
        services.AddSingleton<CharacterInfoRefreshService>(); // on-start + hourly affiliation refresh for all known characters
        services.AddSingleton<EsiFleetSyncService>(); // 5 s boss-side roster mirror for linked in-game fleets
        services.AddSingleton<EsiSelfReportService>(); // 60 s member self-report for coupled server-fleets we're a non-boss member of
        services.AddSingleton<SkillRefreshService>(); // on-start + 120 s (ESI skill-endpoint TTL) skill+queue refresh for all coupled characters
        services.AddSingleton<ImplantRefreshService>(); // on-start + 120 s implant refresh for all coupled characters
        services.AddSingleton<EveUtils.Client.Platform.EveClientPresenceService>(); // 5 s sweep for running EVE clients → character-list badge
    }

    // Per-instance data dir + DB so two clients (EVEUTILS_INSTANCE=A / =B) don't share state —
    // needed to demo the fleet sync (ship/DPS) between two clients on one machine. Public so the Settings dialog can
    // open it in the OS file browser ("Show Data Folder").
    public static string DataDirectory()
    {
        var instance = Environment.GetEnvironmentVariable("EVEUTILS_INSTANCE")?.Trim();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveUtils");
        var dir = string.IsNullOrEmpty(instance) ? root : Path.Combine(root, instance);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ClientDbConnectionString() =>
        $"Data Source={Path.Combine(DataDirectory(), "client.db")}";
}
