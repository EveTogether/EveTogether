using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-258: what EVE wrote to a character's gamelog while the app was closed — lost until now, because
/// <c>GameLogWatcher.Start</c> baselines every file to its current length rather than replaying history. RESUME
/// (ET-254) is the one moment this is read back, once, beside the live tail rather than through it.
///
/// Line shapes below are copied from Jithran's own real gamelogs (<c>Documents\EVE\logs\Gamelogs</c>, read-only),
/// parameterised only by timestamp/amount/target — never invented.
/// </summary>
public sealed class GamelogCatchUpTests
{
    // The dungeon id alone resolves TYPE (RunTypeCatalogue.For, ET-228) — same rule FleetRunMiningSharingTests uses.
    private static readonly SdeSite MetaliminalSite = new(10312, "Metaliminal Meteoroid: Amarr Mining", ArchetypeId: 70,
        ArchetypeName: null, FactionId: null, FactionName: null, Description: null, DedRating: null,
        IsShipRestricted: false, AllowedShipGroups: []);

    private static string _Timestamp(DateTime atUtc) => atUtc.ToString("yyyy.MM.dd HH:mm:ss");

    private static string _BountyLine(DateTime atUtc, string isk) =>
        $"[ {_Timestamp(atUtc)} ] (bounty) <font size=12><b><color=0xff00aa00>{isk} ISK</b><color=0x77ffffff> added to next bounty payout";

    private static string _MinedLine(DateTime atUtc, int units, string ore) =>
        $"[ {_Timestamp(atUtc)} ] (mining) <color=0x77ffffff>You mined <font size=12><color=#ff8dc169>{units}<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>{ore}";

    private static string _ResidueLine(DateTime atUtc, int units) =>
        $"[ {_Timestamp(atUtc)} ] (mining) <color=0x77ffffff>Additional <font size=12><color=#ffff454b>{units}<color=0x77ffffff><font size=10> units depleted from asteroid as residue";

    private static string _IncomingCombatLine(DateTime atUtc, int amount, string target) =>
        $"[ {_Timestamp(atUtc)} ] (combat) <color=0xffcc0000><b>{amount}</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>{target}</b><font size=10><color=0x77ffffff> - Penetrates";

    private static string _SessionHeader(string character, DateTime startedAtUtc) =>
        "------------------------------------------------------------\n"
        + $"  Gamelog\n  Listener: {character}\n  Session Started: {_Timestamp(startedAtUtc)}\n"
        + "------------------------------------------------------------\n";

    /// <summary>
    /// The acceptance criteria in one test: the app falls away mid-site, three kinds of gamelog lines land while
    /// nothing is watching — split across two files, because EVE starts a new one per session — and RESUME catches
    /// all of it up exactly once, without moving the live tail or feeding the DPS graph a single historical hit.
    /// </summary>
    [AvaloniaFact]
    public async Task ResumingACrashedRun_CatchesUpBountyMiningAndEnemies_OnceEach_WithoutFeedingTheLiveTail()
    {
        var toasts = new RecordingToastService();
        using var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<IToastService>(toasts));
        IDispatcher dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ActivityWindowViewModel first = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await first.StartRunCommand.ExecuteAsync(null);
        Guid runId = first.RunId!.Value;

        // Live, before the crash — the watcher was tailing, so this one already landed on the run. Must not be
        // recounted by the catch-up read below.
        await harness.WriteLineAsync(ActivityWindowHarness.BountyLine("67.500"));
        await ActivityWindowHarness.WaitUntil(() => first.BountyIsk == 67_500m);

        // The last confirmed-alive moment (ET-254's heartbeat) — real wall-clock time, since the catch-up window
        // below is bounded by DateTime.UtcNow at resume, not by these gamelog lines' own (fixed, far-future 2030)
        // timestamps. Whole-second offsets from here on: a gamelog line's own timestamp has no finer resolution
        // than a second, so an offset smaller than that would round-trip through the log format to the very same
        // second lastAliveUtc itself falls in, and land on the excluded side of the floor by construction.
        DateTime lastAliveUtc = DateTime.UtcNow;
        await dispatcher.Send(new TouchRunAliveCommand(runId, lastAliveUtc), cancellationToken);

        // The app "closes": nothing tails the directory any more, and the process (window included) goes away with
        // the run still Running — the same crash ET-254 already covers.
        harness.Services.GetRequiredService<GamelogWatcherService>().Stop();
        first.Dispose();

        string sameSessionFile = Path.Combine(harness.GamelogDirectory, "20300101_120000_90000001.txt");
        string secondSessionFile = Path.Combine(harness.GamelogDirectory, "20300101_130000_90000001.txt");

        // At the exact floor — already known-alive the moment it was written — and one further back still: neither
        // may be read again.
        await File.AppendAllTextAsync(sameSessionFile,
            _BountyLine(lastAliveUtc.AddSeconds(-5), "1.000") + "\n" + _BountyLine(lastAliveUtc, "2.000") + "\n",
            cancellationToken);

        // What EVE wrote while the app was closed: a bounty and a full mining cycle (its residue line included, in
        // the same second, as measured against Jithran's own logs) in the file the session was already using, and
        // one enemy sighting in a brand-new file — EVE starts a new gamelog per client session, so a crash spanning
        // a reconnect can split one character's lines across more than one file.
        await File.AppendAllTextAsync(sameSessionFile,
            _BountyLine(lastAliveUtc.AddSeconds(1), "67.500") + "\n"
            + _MinedLine(lastAliveUtc.AddSeconds(2), 13, "Amperum Mutanite") + "\n"
            + _ResidueLine(lastAliveUtc.AddSeconds(2), 12) + "\n",
            cancellationToken);
        await File.WriteAllTextAsync(secondSessionFile,
            _SessionHeader(ActivityWindowHarness.CharacterName, lastAliveUtc.AddSeconds(2))
            + _IncomingCombatLine(lastAliveUtc.AddSeconds(3), 48, "Centii Servant") + "\n",
            cancellationToken);

        // Real time has to pass the last gap line's own timestamp before resuming — the catch-up window's upper
        // bound is the real moment RESUME runs, not anything written into a file.
        await Task.Delay(3500, cancellationToken);

        // The restart: the leftover Running row is stopped at the heartbeat, and the watcher comes back and
        // baselines every file — including the two above — to its current length, exactly the behaviour that loses
        // this history on the live path.
        await dispatcher.Send(new StopRunsLeftRunningCommand(DateTime.UtcNow), cancellationToken);
        await harness.Services.GetRequiredService<GamelogWatcherService>().RestartAsync(cancellationToken);

        ActivityWindowViewModel resumed = new(ActivityKind.Site, harness.Services);
        resumed.UseCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
        resumed.ResumeRun(runId);

        // Nothing here is a poll for a background pump — the catch-up read runs synchronously inside LoadAsync.
        Assert.False(harness.Services.GetRequiredService<GamelogClientService>().HasLocalTracker(ActivityWindowHarness.CharacterName));
        await resumed.LoadAsync();
        // Drains the UI-thread post BountyObserved queued (ActivityWindowViewModel._OnBountyObserved) before this
        // window — and then the harness — is disposed below.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(ActivityRunState.Running, resumed.RunState);
        Assert.Equal(67_500m + 67_500m, await _BountyTotalAsync(harness, runId)); // the live one, plus the one caught up — not the two excluded, not double

        RunMiningEntry mining = Assert.Single(await _MiningEntriesAsync(harness, runId));
        Assert.Equal("Amperum Mutanite", mining.OreType);
        Assert.Equal(13, mining.Units);
        Assert.Equal(0, mining.CriticalUnits);
        Assert.Equal(12, mining.ResidueUnits);

        var observed = Assert.Single(resumed.Enemies().EnemyObservations);
        Assert.Equal("Centii Servant", observed.EnemyName);

        // AC-2: the live DPS graph gets no historical peaks. The catch-up combat line never reached AddHitAsync, so
        // this character still has no local tracker at all — not one that merely decayed back to zero.
        Assert.False(harness.Services.GetRequiredService<GamelogClientService>().HasLocalTracker(ActivityWindowHarness.CharacterName));

        (string Title, string? Message, ToastKind Kind) toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Caught up", toast.Title);
        string message = toast.Message ?? string.Empty;
        Assert.Contains("bounty payout", message);
        Assert.Contains("mining cycle", message);
        Assert.Contains("enem", message);

        resumed.Dispose();
    }

    /// <summary>AC-3, and the other half of the guard: the in-window STOP/START pause shares
    /// <c>_ResumeAdoptedRunAsync</c> with ET-254's deliberate resume, but never names a run to resume — the live
    /// tail never stopped for it, so nothing here may ever run a catch-up read on top of what the tail already
    /// processed.</summary>
    [AvaloniaFact]
    public async Task PausingAndRestartingInTheSameWindow_NeverRunsACatchUpRead()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();

        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        Guid runId = model.RunId!.Value;

        model.StopRun(DateTime.UtcNow);

        // What a catch-up read (wrongly triggered) would pick up — the live tail is still running throughout this
        // whole test, so it already saw everything there is to see, and StartRunAsync's own pause never asks again.
        await harness.WriteLineAsync(ActivityWindowHarness.BountyLine("67.500"));
        await ActivityWindowHarness.WaitUntil(() => model.BountyIsk == 67_500m);

        await model.StartRunCommand.ExecuteAsync(null); // the pause resumes — RunId known, RunState Stopped
        Assert.Equal(ActivityRunState.Running, model.RunState);

        Assert.Equal(67_500m, await _BountyTotalAsync(harness, runId));
        model.Dispose();
    }

    /// <summary>
    /// ET-262: the "pale shadow" line is a notify line, which the live tail already routed to
    /// <see cref="GamelogClientService.AddNotify"/> — but the catch-up switch in
    /// <c>ActivityWindowViewModel._CatchUpGamelogAsync</c> had no <c>NotifyEvent</c> case at all, so a site that
    /// completed while the app was closed never set its outcome once RESUME caught the rest up. Real fixture line
    /// (Documents\EVE\logs\Gamelogs\20260829_075933_90250177.txt:641).
    /// </summary>
    [AvaloniaFact]
    public async Task ResumingACrashedRun_CatchesUpThePaleShadowLine_AndSetsTheOutcome()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        IDispatcher dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ActivityWindowViewModel first = await harness.OpenAsync();
        first.MatchedSites = [MetaliminalSite];
        await harness.StartWatchingAsync();
        await first.StartRunCommand.ExecuteAsync(null);
        Guid runId = first.RunId!.Value;

        DateTime lastAliveUtc = DateTime.UtcNow;
        await dispatcher.Send(new TouchRunAliveCommand(runId, lastAliveUtc), cancellationToken);

        harness.Services.GetRequiredService<GamelogWatcherService>().Stop();
        first.Dispose();

        string sameSessionFile = Path.Combine(harness.GamelogDirectory, "20300101_120000_90000001.txt");
        await File.AppendAllTextAsync(sameSessionFile,
            $"[ {_Timestamp(lastAliveUtc.AddSeconds(1))} ] (notify) Miner II deactivates as it finds the resource "
            + "it was harvesting a pale shadow of its former glory.\n",
            cancellationToken);

        await Task.Delay(1500, cancellationToken);

        await dispatcher.Send(new StopRunsLeftRunningCommand(DateTime.UtcNow), cancellationToken);
        await harness.Services.GetRequiredService<GamelogWatcherService>().RestartAsync(cancellationToken);

        ActivityWindowViewModel resumed = new(ActivityKind.Site, harness.Services);
        resumed.MatchedSites = [MetaliminalSite];
        resumed.UseCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
        resumed.ResumeRun(runId);
        // Subscribed before LoadAsync, the way the real window's own section is already standing before RESUME's
        // catch-up read runs inside it — a section built afterwards would miss the event outright.
        using HomefrontWindowSectionViewModel section = new(resumed);
        await resumed.LoadAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        DateTime clock = DateTime.UtcNow;
        for (int tick = 0; tick < 30; tick++)
        {
            clock = clock.AddSeconds(1);
            resumed.Refresh(clock);
            section.Refresh(clock);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, cancellationToken);
        }

        Run run = await _RunAsync(harness, runId);
        Assert.Equal(HomefrontOutcome.Completed, run.HomefrontOutcome);
        Assert.True(run.HomefrontOutcomeFromGameLog);

        resumed.Dispose();
    }

    private static async Task<Run> _RunAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
    }

    private static async Task<decimal> _BountyTotalAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<RunBountyEntry>().AsNoTracking()
            .Where(entry => entry.RunId == runId).SumAsync(entry => entry.Isk);
    }

    private static async Task<List<RunMiningEntry>> _MiningEntriesAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<RunMiningEntry>().AsNoTracking()
            .Where(entry => entry.RunId == runId).ToListAsync();
    }
}
