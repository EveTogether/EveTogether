using System;
using System.Linq;
using System.Threading;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Avalonia;
using EveUtils.Client.Composition;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using EveUtils.Shared.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace EveUtils.Client;

sealed class Program
{
    /// <summary>Composition root, exposed to the Avalonia <c>App</c> so it can build the ViewModel.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        // Must stay the first statement: installing, updating and uninstalling all re-run this executable with
        // arguments Velopack handles here and then exits on. Anything above it runs the EF migration and the
        // background services against the user's data during an installation step.
        VelopackApp.Build().Run();

        // Last-chance net (ET-197): armed as early as possible so a fault anywhere further in startup still
        // leaves a trace. Writes straight to app-errors.jsonl, bypassing ILogger/DI — see CrashLog for why.
        CrashLog.Install(ClientServices.DataDirectory());

        // The UI is English-only (§2) and the client's formatting helpers already pass InvariantCulture, so pin
        // the process instead of letting numbers follow the OS locale — that is the one element that would
        // silently differ per machine ("9,0 m³/s" next to an invariant "1.5B ISK" in the same window).
        ClientCulture.Apply();

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
        RunResilient(Services.GetRequiredService<EveUtils.Client.Esi.ShipFitDetectionService>().StartAsync(refreshCts.Token), "ship-fit-detection");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Skills.SkillRefreshService>().StartAsync(refreshCts.Token), "skill-refresh");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Implants.ImplantRefreshService>().StartAsync(refreshCts.Token), "implant-refresh");
        RunResilient(Services.GetRequiredService<EveUtils.Client.Platform.EveClientPresenceService>().StartAsync(refreshCts.Token), "eve-client-presence");
        StartResumeRecovery(refreshCts.Token);
        // Automatic EVE settings sync (ET-60): does nothing at all unless the user configured and enabled it, and
        // then only while every EVE client is closed.
        RunResilient(Services.GetRequiredService<EveUtils.Client.EveSettings.AutoSettingsSyncService>().StartAsync(refreshCts.Token), "eve-settings-auto-sync");
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

        // Deliberately triggers one of the last-chance crash-log paths (ET-197), to measure what CrashLog
        // actually writes to app-errors.jsonl rather than just trust that it does. Not part of the GUI path.
        if (args.FirstOrDefault(a => a.StartsWith("--crash-test=", StringComparison.Ordinal)) is { } crashArg)
        {
            switch (crashArg["--crash-test=".Length..])
            {
                case "appdomain":
                    // A bare Thread has nothing else to catch this — it can only ever reach AppDomain.UnhandledException.
                    new Thread(() => throw new InvalidOperationException("ET-197 deliberate crash-test: appdomain")).Start();
                    Thread.Sleep(Timeout.Infinite); // the runtime terminates the process once the handler returns
                    break;
                case "task":
                    RunAndDropFaultingTask();
                    // A Debug-build JIT frame (this one: Main) reports its locals conservatively for its whole
                    // body, so the dropped Task above would still be considered reachable here — confined to its
                    // own non-inlined method instead, so its frame (and the only reference) is gone once it returns.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Console.WriteLine("crash-test=task: done, check app-errors.jsonl");
                    break;
                default:
                    Console.WriteLine($"unknown --crash-test mode: {crashArg}");
                    break;
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

        _ = Services.GetRequiredService<EveUtils.Client.Fleet.FleetRunGroupCodeCoordinator>();
        // Up before any run window, so the fleet's clock knows who went in before this pilot's window opened (ET-243).
        _ = Services.GetRequiredService<EveUtils.Client.Fleet.FleetRunLegs>();
        // Likewise for what the others share of the run (ET-242): their loot is there when this pilot's window opens.
        _ = Services.GetRequiredService<EveUtils.Client.Fleet.FleetRunShares>();
        // And the commander's homefront attendance list (ET-230): written onto this pilot's runs with no window open.
        _ = Services.GetRequiredService<EveUtils.Client.Fleet.FleetRunAttendance>();

        // Brings the run window up on every member's screen when the FC starts — without taking focus (ET-105).
        _ = Services.GetRequiredService<EveUtils.Client.Runs.FleetRunWindowPresenter>();

        // Publishes a fleet run to its fleet's server on SAVE and pulls a group mate's the moment the server says it
        // arrived (ET-245). Up before the startup auto-save below, so a fleet run that save commits is queued too.
        _ = Services.GetRequiredService<EveUtils.Client.Runs.FleetRunAutoPublisher>();

        // Settle up the client-only fleets that were left running when this app was last closed (ET-167). Awaited
        // rather than fired off, and awaited BEFORE the publisher starts: the publisher's first tick stamps those
        // very fleets as seen, and a reckoning racing it would read the silence it was meant to measure as presence.
        // A couple of local reads; a failure here may not keep the app from opening.
        try
        {
            Services.GetRequiredService<EveUtils.Client.Fleet.LocalFleetAutoStopService>()
                .ReconcileAsync(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bg:local-fleet-auto-stop] failed: {ex}");
        }

        // Fleet metric publisher: shares the active fleet's metrics at ~1 Hz. GUI mode only —
        // the headless test drives the tick itself.
        Services.GetRequiredService<EveUtils.Client.Fleet.FleetMetricPublisher>().Start();

        // Live gamelog watcher: tails the configured/auto-detected EVE gamelog directory and feeds real
        // per-character DPS into the gamelog service (replaces the synthetic feeder for live data).
        RunResilient(Services.GetRequiredService<EveUtils.Client.Gamelog.GamelogWatcherService>().StartAsync(refreshCts.Token), "gamelog-watcher");

        // A run left on the clock by a previous process is ended here, before any window exists to adopt it. Raymond
        // opened the app on 2026-09-04 and was handed a run that had started the previous morning — ELAPSED 1467:38 —
        // because quitting with a run going leaves the row Running and nothing afterwards disagrees.
        //
        // Below every --diagnostic argument above, all of which return before this line: --smoke and --sde-check open
        // the same database, and a diagnostic run has no business ending a run the pilot is flying.
        //
        // ponytail: "a previous process" is really "no other process", which holds because one data directory is one
        // client — that is what EVEUTILS_INSTANCE exists to keep true. A second launch against the same directory
        // would stop the first one's run. Give the row the session that owns it if that ever stops being true.
        using (var scope = Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            Result<IReadOnlyList<StoppedRunDto>> stopped = dispatcher
                .Send(new StopRunsLeftRunningCommand(DateTime.UtcNow)).GetAwaiter().GetResult();
            if (stopped.IsSuccess && stopped.Value is { Count: > 0 } stoppedRuns)
            {
                Console.Error.WriteLine($"[startup] stopped {stoppedRuns.Count} run(s) left running by a previous session");
                // Staged rather than shown here: there is no window yet for a toast to attach to (ET-254). The main
                // window's own Opened handler asks for these once it exists.
                Services.GetRequiredService<EveUtils.Client.Runs.StartupResumeNoticeService>().Stage(stoppedRuns);
            }

            // And the ones stopped a day ago and never finished are committed as they stand (ET-179). After the line
            // above on purpose: a run this session's predecessor left going was stopped a moment ago, so it is the
            // pilot's to finish for the next day rather than the app's to save now.
            Result<int> autoSaved = dispatcher
                .Send(new SaveRunsLeftUnfinishedCommand(DateTime.UtcNow)).GetAwaiter().GetResult();
            if (autoSaved.IsSuccess && autoSaved.Value > 0)
                Console.Error.WriteLine($"[startup] saved {autoSaved.Value} run(s) left unfinished for over a day");

            // A saved activity whose TOTAL ISK was added up by another set of sources than this build registers —
            // saved before ET-256 stored a total at all, or before a later source existed — is added up again once,
            // so the runs overview and "ISK today" agree with the detail screen from the first look.
            Result<int> rebuilt = dispatcher
                .Send(new RebuildActivitySummariesCommand(OnlyWhenOutdated: true)).GetAwaiter().GetResult();
            if (rebuilt.IsSuccess && rebuilt.Value > 0)
                Console.Error.WriteLine($"[startup] added up the ISK of {rebuilt.Value} saved run(s) again");

            // One-time repair for runs started before ET-228 kept a homefront's dungeon id (Run.SiteTypeId always
            // came back as 0). Idempotent and cheap once repaired: a run whose id already matches its exact site
            // name is left alone, so this is a no-op on every later startup. On the first start after an SDE
            // schema bump the SDE is not available yet at this point, so this call repairs nothing — MainWindowViewModel
            // .RunSdeImportPopupAsync runs it again once that same session's SDE import finishes (ET-261).
            Result<int> repairedHomefronts = dispatcher
                .Send(new RepairHomefrontSiteTypeIdsCommand()).GetAwaiter().GetResult();
            if (repairedHomefronts.IsSuccess && repairedHomefronts.Value > 0)
                Console.Error.WriteLine($"[startup] repaired the homefront type of {repairedHomefronts.Value} run(s)");

            // One-time repair for ET-260: a mission flown with more than one own toon wrote the same reward
            // parameters onto every one of that group's runs, before the run window learned to write them onto only
            // the character who actually accepted the mission. Idempotent: a group already down to one run with
            // parameters is left alone, so this is a no-op on every later startup.
            Result<int> dedupedMissionRewards = dispatcher
                .Send(new DedupeMissionRewardParametersCommand()).GetAwaiter().GetResult();
            if (dedupedMissionRewards.IsSuccess && dedupedMissionRewards.Value > 0)
                Console.Error.WriteLine($"[startup] removed duplicated mission rewards from {dedupedMissionRewards.Value} run(s)");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Reaching this line means the desktop lifetime ended on its own rather than the process being cut out
        // from under it — a missing line here after a session is the tell that it wasn't (ET-197 acceptance #4).
        CrashLog.WriteShutdownMarker("desktop lifetime exited");
    }

    /// <summary>Runs a task that always faults and waits for it to finish without observing the exception, so a
    /// caller can force a collection afterwards and prove <see cref="CrashLog"/> catches it via
    /// <see cref="TaskScheduler.UnobservedTaskException"/> (ET-197). Its own method so the frame — and the only
    /// reference to the task — is gone once it returns, instead of lingering in Main's until the process exits.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void RunAndDropFaultingTask()
    {
        var faulting = Task.Run(() => throw new InvalidOperationException("ET-197 deliberate crash-test: task"));
        while (!faulting.IsCompleted) Thread.Sleep(10);
    }

    /// <summary>
    /// Starts a fire-and-forget background <paramref name="task"/> and logs (never swallows) a fault so a
    /// dead background service is observable instead of silently vanishing.
    /// </summary>
    static void RunResilient(Task task, string name) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[bg:{name}] crashed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>
    /// Wires waking from sleep to the two things that are certainly stale afterwards (ET-121): the server bus
    /// connections, whose sockets died while the machine was off, and the ESI tokens, which aged past their hour.
    /// Neither notices on its own quickly — the streams wait out a 45 s receive deadline and the token loop wakes on
    /// its own 60 s tick, and on the morning this ticket came from that added up to watches that stayed dead for
    /// hours. Nudging both is idempotent, so a false positive costs one reconnect.
    /// </summary>
    static void StartResumeRecovery(CancellationToken cancellationToken)
    {
        var watcher = Services.GetRequiredService<EveUtils.Client.Platform.SystemResumeWatcher>();
        watcher.Resumed += _ =>
        {
            RunResilient(Services.GetRequiredService<RemoteBusConnectionManager>()
                .ReconnectAllAsync(cancellationToken), "resume-reconnect");
            RunResilient(RecheckEsiTokensAsync(cancellationToken), "resume-token-recheck");
        };
        RunResilient(watcher.StartAsync(cancellationToken), "system-resume-watch");
    }

    /// <summary>Re-checks every known character's ESI token now rather than on the refresh loop's next tick.</summary>
    static async Task RecheckEsiTokensAsync(CancellationToken cancellationToken)
    {
        var registry = Services.GetRequiredService<ICharacterRegistry>();
        var refresh = Services.GetRequiredService<ClientTokenRefreshService>();
        foreach (var character in await registry.GetAllAsync(cancellationToken))
            if (character.EsiCharacterId is { } characterId)
                await refresh.EnsureValidAsync(characterId, cancellationToken);
    }

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
