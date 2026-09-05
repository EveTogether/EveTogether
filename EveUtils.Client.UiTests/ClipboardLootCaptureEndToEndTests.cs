using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Data;
using EveUtils.Client.Platform;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-190: every existing clipboard/loot test either constructs <see cref="ClipboardLootCapture"/> by hand (bypassing
/// DI resolution) or opens the activity window through <c>RecordingDialogService</c> (which never runs
/// <c>LoadAsync</c>, so no <see cref="Run"/> row is ever created). Nowhere is the real chain exercised end to end:
/// App.axaml.cs's own order (all four clipboard consumers resolved through DI, the real <see cref="DialogService"/>
/// with an owner window, ET-158's auto-start actually opening a window and writing the run) followed by a second,
/// unrelated clipboard copy landing on that run. This test is that chain, to find out whether it is the wiring
/// itself — not any single handler — that lost the loot.
/// </summary>
public sealed class ClipboardLootCaptureEndToEndTests
{
    private const int CharacterId = 90000001;
    private const string CharacterName = "Test Pilot";

    [AvaloniaFact]
    public async Task ARealAutoStartedRun_StillAcceptsLootCopiedRightAfterIt()
    {
        var source = new FakeClipboardChangeSource();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(FakeSdeAccessor.WithSampleFit());
            services.AddSingleton<IToastService>(new RecordingToastService());
            services.AddSingleton<IClipboardChangeSource>(source);
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(inGame: true, CharacterId));
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(CharacterName, CharacterId));

        // App.axaml.cs's own order: the owner is set, then all four clipboard consumers are resolved through DI
        // (forcing their subscribing constructors to run), and only then does the watch start.
        var dialogs = instance.Services.GetRequiredService<DialogService>();
        var owner = new Window();
        owner.Show();
        dialogs.SetOwner(owner);

        _ = instance.Services.GetRequiredService<ClipboardFitImportOffer>();
        _ = instance.Services.GetRequiredService<ClipboardSignatureOffer>();
        _ = instance.Services.GetRequiredService<ClipboardLootCapture>();
        _ = instance.Services.GetRequiredService<ClipboardMissionOffer>();
        var watch = instance.Services.GetRequiredService<ClipboardWatchService>();
        await watch.SetEnabledAsync(true);

        // One fully-scanned combat site: ET-158 starts its run without a button, through the real DialogService and
        // a real ActivityWindow.
        Copy(source, "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU");
        await ActivityWindowHarness.WaitUntil(() => dialogs.ActivityWindow?.DataContext is
            EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel { RunId: not null });

        Guid runId = ((EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel)dialogs.ActivityWindow!.DataContext!).RunId!.Value;
        Assert.Equal(RunState.Running, await _RunStateAsync(instance, runId));

        // The second, unrelated copy ET-190 is about: inventory loot, right after the run is going.
        Copy(source, "Rifter\t1\r\nDamage Control II\t2");
        await instance.Services.GetRequiredService<ClipboardLootCapture>().LastStore;

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunLootCapture[] captures = await db.Set<RunLootCapture>().Where(c => c.RunId == runId).ToArrayAsync();
        Assert.True(captures.Length > 0,
            "the auto-started run took the site but not the loot copied right after it");

        owner.Close();
    }

    /// <summary>
    /// c69fec1 (2026-09-04): a second, different site copied over a running run no longer closes it out and starts
    /// the new one — it waits (SAVE/DISCARD/KEEP), and <c>ApplySignature</c> calls <c>StopRun</c> on the run that
    /// is going. Nothing then makes it Running again until the pilot answers the toast. If he does not — because he
    /// is mid-fight and it is a transient toast, not a modal — every ctrl+c after that stray copy runs into
    /// <c>RunningRunLookup</c> with zero Running runs. This measures what happens to loot copied during that wait.
    /// </summary>
    [AvaloniaFact]
    public async Task LootCopiedWhileASecondSiteIsWaitingBehindTheRunningOne_IsWhatET190Reports()
    {
        var source = new FakeClipboardChangeSource();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(FakeSdeAccessor.WithSampleFit());
            services.AddSingleton<IToastService>(new RecordingToastService());
            services.AddSingleton<IClipboardChangeSource>(source);
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(inGame: true, CharacterId));
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(CharacterName, CharacterId));

        var dialogs = instance.Services.GetRequiredService<DialogService>();
        var owner = new Window();
        owner.Show();
        dialogs.SetOwner(owner);

        _ = instance.Services.GetRequiredService<ClipboardFitImportOffer>();
        _ = instance.Services.GetRequiredService<ClipboardSignatureOffer>();
        _ = instance.Services.GetRequiredService<ClipboardLootCapture>();
        _ = instance.Services.GetRequiredService<ClipboardMissionOffer>();
        var watch = instance.Services.GetRequiredService<ClipboardWatchService>();
        await watch.SetEnabledAsync(true);

        Copy(source, "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU");
        await ActivityWindowHarness.WaitUntil(() => dialogs.ActivityWindow?.DataContext is
            EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel { RunId: not null });
        Guid runId = ((EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel)dialogs.ActivityWindow!.DataContext!).RunId!.Value;

        // A stray second site — an overview double-copy, a scan window still open behind EVE, anything — lands
        // while he is still flying the first. It is not answered: he is mid-fight, and it is a toast, not a dialog.
        Copy(source, "BBB-002\tCosmic Signature\tCombat Site\tGuardian's Gala\t100.0%\t1.10 AU");
        await ActivityWindowHarness.WaitUntil(() => _RunStateAsync(instance, runId).Result == RunState.Stopped);

        Copy(source, "Rifter\t1\r\nDamage Control II\t2");
        await instance.Services.GetRequiredService<ClipboardLootCapture>().LastStore;

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunLootCapture[] captures = await db.Set<RunLootCapture>().Where(c => c.RunId == runId).ToArrayAsync();
        Assert.True(captures.Length > 0,
            "loot copied while a second site waits behind the running one was not recorded against it");

        owner.Close();
    }

    /// <summary>
    /// The combination the docstrings on <c>RunningRunLookup</c> and <c>AddRunLootCaptureCommandHandler</c> both
    /// point at without quite saying it: a single stray "waiting" copy (c69fec1) does not break loot on its own —
    /// <see cref="LootCopiedWhileASecondSiteIsWaitingBehindTheRunningOne_IsWhatET190Reports"/> is proof, it still
    /// finds the one Stopped candidate. Add ONE leftover Stopped-and-never-saved run — Raymond found eleven of them
    /// on 2026-09-04 — beside it, and <c>RunningRunLookup</c> had two Stopped candidates and zero Running ones,
    /// which is ET-190 exactly: "N runs are running", loot not recorded. With the fix, <c>ClipboardLootCapture</c>
    /// hands over the open activity window's own <c>RunId</c> (<c>IDialogService.ActivityWindowRunId</c>) and the
    /// lookup answers with that run outright — it never has to count the leftover at all, so it stays untouched
    /// (still Stopped, still there): this is not the leftover-runs cleanup, only loot finding its way past it.
    /// </summary>
    [AvaloniaFact]
    public async Task LootCopiedWhileWaiting_StillLandsOnTheRightRun_WithAnOldAbandonedStoppedRunAlsoPresent()
    {
        var source = new FakeClipboardChangeSource();
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(FakeSdeAccessor.WithSampleFit());
            services.AddSingleton<IToastService>(new RecordingToastService());
            services.AddSingleton<IClipboardChangeSource>(source);
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(inGame: true, CharacterId));
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character(CharacterName, CharacterId));

        // The leftover: a run from an earlier session, stopped and never saved or discarded — exactly what Raymond
        // found eleven of. Written directly, the way a stale row simply sitting in the store would be. The fix must
        // not touch it — this ticket is about loot finding its own run, not about sweeping this one up.
        var abandonedRunId = Guid.CreateVersion7();
        await using (ClientDbContext seed = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync())
        {
            seed.Set<Run>().Add(new Run
            {
                Id = abandonedRunId,
                CharacterId = (long)CharacterId,
                ActivityKind = ActivityKind.Site,
                StartedAtUtc = DateTime.UtcNow.AddDays(-1),
                State = RunState.Stopped,
                SiteName = "Yesterday's abandoned site",
            });
            await seed.SaveChangesAsync();
        }

        var dialogs = instance.Services.GetRequiredService<DialogService>();
        var owner = new Window();
        owner.Show();
        dialogs.SetOwner(owner);

        _ = instance.Services.GetRequiredService<ClipboardFitImportOffer>();
        _ = instance.Services.GetRequiredService<ClipboardSignatureOffer>();
        _ = instance.Services.GetRequiredService<ClipboardLootCapture>();
        _ = instance.Services.GetRequiredService<ClipboardMissionOffer>();
        var watch = instance.Services.GetRequiredService<ClipboardWatchService>();
        await watch.SetEnabledAsync(true);

        Copy(source, "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU");
        await ActivityWindowHarness.WaitUntil(() => dialogs.ActivityWindow?.DataContext is
            EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel { RunId: not null });
        Guid runId = ((EveUtils.Client.ViewModels.Activity.ActivityWindowViewModel)dialogs.ActivityWindow!.DataContext!).RunId!.Value;

        Copy(source, "BBB-002\tCosmic Signature\tCombat Site\tGuardian's Gala\t100.0%\t1.10 AU");
        await ActivityWindowHarness.WaitUntil(() => _RunStateAsync(instance, runId).Result == RunState.Stopped);

        var toasts = (RecordingToastService)instance.Services.GetRequiredService<IToastService>();
        Copy(source, "Rifter\t1\r\nDamage Control II\t2");
        await instance.Services.GetRequiredService<ClipboardLootCapture>().LastStore;

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunLootCapture[] captures = await db.Set<RunLootCapture>().Where(c => c.RunId == runId).ToArrayAsync();
        Assert.True(captures.Length > 0,
            "loot copied while a second site waits behind the running one, beside an old abandoned Stopped run, "
            + "was rejected as ambiguous instead of landing on the window's own run");
        Assert.DoesNotContain(toasts.Toasts, toast => toast.Title == "Loot not recorded");
        // Untouched: still there, still Stopped — the fix finds the right run, it does not clean up the other one.
        Assert.Equal(RunState.Stopped, await _RunStateAsync(instance, abandonedRunId));
        Assert.Empty(await db.Set<RunLootCapture>().Where(c => c.RunId == abandonedRunId).ToArrayAsync());

        owner.Close();
    }

    private static async Task<RunState> _RunStateAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return (await db.Set<Run>().AsNoTracking().SingleAsync(r => r.Id == runId)).State;
    }

    private static void Copy(FakeClipboardChangeSource source, string text)
    {
        source.ClipboardText = text;
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public string? ClipboardText { get; set; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);

        public void RaiseChanged() => Changed?.Invoke();
    }
}
