using System;
using System.Linq;
using System.Threading;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Avalonia;
using EveUtils.Client.Composition;
using EveUtils.Client.Esi;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client;

sealed class Program
{
    /// <summary>Composition root, exposed to the Avalonia <c>App</c> so it can build the ViewModel.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        Services = ClientServices.Build();

        // Apply the client migration stack via the factory (short-lived context).
        using (var scope = Services.CreateScope())
        {
            using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContext();
            db.Database.Migrate();
        }

        // Start background services (token refresh, ESI cache purge) — the client has no IHost so we start manually.
        // Fire-and-forget discards a faulting Task silently, so route every start through RunResilient, which
        // logs an unobserved fault instead of letting a dead background task disappear.
        var refreshCts = new CancellationTokenSource();
        RunResilient(Services.GetRequiredService<ClientTokenRefreshService>().StartAsync(refreshCts.Token), "token-refresh");
        RunResilient(Services.GetRequiredService<EsiCachePurgeService>().StartAsync(refreshCts.Token), "esi-cache-purge");
        RunResilient(Services.GetRequiredService<EveServerStatusService>().StartAsync(refreshCts.Token), "eve-server-status");
        RunResilient(Services.GetRequiredService<EsiMarketPriceService>().StartAsync(refreshCts.Token), "esi-market-prices");
        RunResilient(Services.GetRequiredService<CharacterInfoRefreshService>().StartAsync(refreshCts.Token), "character-info-refresh");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Esi.EsiFleetSyncService>().StartAsync(refreshCts.Token), "esi-fleet-sync");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Esi.EsiSelfReportService>().StartAsync(refreshCts.Token), "esi-self-report");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Skills.SkillRefreshService>().StartAsync(refreshCts.Token), "skill-refresh");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Implants.ImplantRefreshService>().StartAsync(refreshCts.Token), "implant-refresh");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Platform.EveClientPresenceService>().StartAsync(refreshCts.Token), "eve-client-presence");
        // Opt-in local widget API host: reads the persisted enabled/port settings and starts the loopback server
        // only when the user enabled it (default off). No-op when disabled.
        RunResilient(Services.GetRequiredService<EveUtils.Client.LocalApi.ILocalApiServer>().StartAsync(refreshCts.Token), "local-api");
        // One-off content-hash backfill for rows imported before the column existed. Async fire-and-forget (no
        // sync-over-async / GetResult): it only affects dedup of future imports, so it need not block startup.
        RunResilient(BackfillFitHashesAsync(), "fit-hash-backfill");

        // Headless verification of the data/CQRS layer, without starting the GUI.
        if (args.Contains("--smoke"))
        {
            ClientSmoke.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the local API server against the real client services.
        if (args.Contains("--localapi-smoke"))
        {
            ClientLocalApiSmoke.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Diagnostics: run only the SDE update check (network + store), separate from the LoadAsync startup chain.
        if (args.Contains("--sde-check"))
        {
            try
            {
                var importer = Services.GetRequiredService<EveUtils.Shared.Modules.Sde.Import.ISdeImporter>();
                var check = importer.CheckForUpdateAsync().GetAwaiter().GetResult();
                Console.WriteLine($"updateAvailable={check.UpdateAvailable} local={check.Local?.BuildNumber.ToString() ?? "none"} remote={check.Remote.BuildNumber}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CHECK THREW: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is { } inner)
                    Console.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            }
            return;
        }

        // Headless verification of the ESI layer: pivot + handler-chain fallbacks.
        if (args.Contains("--esi-test"))
        {
            Environment.ExitCode = EsiPipelineCheck.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the gRPC transport (TLS + TOFU pinning) against a running server.
        if (args.Contains("--grpc-ping"))
        {
            ClientGrpcPing.RunAsync(Services, args).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the auth-gated remote event bus.
        if (args.Contains("--remote-test"))
        {
            ClientRemoteTest.RunAsync(Services, args).GetAwaiter().GetResult();
            return;
        }

        // Headless full-chain demo: feed synthetic DPS to the server.
        if (args.Contains("--feed"))
        {
            ClientFeed.RunAsync(Services, args).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the fleet metric aggregation + scoping.
        if (args.Contains("--fleet-metric-test"))
        {
            Environment.ExitCode = ClientFleetMetricTest.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of client-only fleets: create locally via the Shared CQRS handlers +
        // client IFleetRepository, add a local toon + external, move a member — without server/gRPC.
        if (args.Contains("--client-fleet-test"))
        {
            Environment.ExitCode = ClientFleetTest.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the external-character 1-day SQLite cache.
        if (args.Contains("--external-cache-test"))
        {
            Environment.ExitCode = ClientExternalCacheTest.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Headless verification of the real gamelog → per-character DPS coupling.
        if (args.Contains("--gamelog-test"))
        {
            Environment.ExitCode = ClientGamelogTest.RunAsync(Services).GetAwaiter().GetResult();
            return;
        }

        // Development-only: couple a seeded dev character without SSO.
        if (args.Contains("--dev-couple"))
        {
            ClientDevCouple.RunAsync(Services, args).GetAwaiter().GetResult();
            return;
        }

        // Headless end-to-end verification of the FleetClient wrapper against a running server.
        if (args.Contains("--fleet-client-test"))
        {
            Environment.ExitCode = ClientFleetClientTest.RunAsync(Services, args).GetAwaiter().GetResult();
            return;
        }

        // Fleet metric publisher: shares the active fleet's metrics at ~1 Hz. GUI mode only —
        // the headless test drives the tick itself.
        Services.GetRequiredService<EveUtils.Client.Fleet.FleetMetricPublisher>().Start();

        // Live gamelog watcher: tails the configured/auto-detected EVE gamelog directory and feeds real
        // per-character DPS into the gamelog service (replaces the synthetic feeder for live data).
        RunResilient(Services.GetRequiredService<EveUtils.Client.Gamelog.GamelogWatcherService>().StartAsync(refreshCts.Token), "gamelog-watcher");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Starts a fire-and-forget background <paramref name="task"/> and logs (never swallows) a fault so a
    /// dead background service is observable instead of silently vanishing.
    /// </summary>
    static void RunResilient(Task task, string name) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[bg:{name}] crashed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>One-off backfill of the fit content-hash for rows predating the column (idempotent dedup).</summary>
    static async Task BackfillFitHashesAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFittingRepository>().BackfillContentHashesAsync();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
