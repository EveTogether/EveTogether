using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Notifications;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

public sealed class AbyssalLootCaptureTests
{
    private const string Container = "Rifter\t1\t0,10 m3\t100,00 ISK\r\nDamage Control II\t2\t0,20 m3\t250,50 ISK";
    private const string SecondContainer = "Nanite Repair Paste\t5\t0,50 m3\t400,00 ISK\r\nEMP S\t100\t1,00 m3\t50,00 ISK";
    private const string ContainerWithAPricelessRow = "Rifter\t1\t0,10 m3\t100,00 ISK\r\nDamage Control II\t2\t0,20 m3\t";

    [AvaloniaTheory]
    [InlineData("Ultraviolet M\t1\tFrequency Crystal\tMedium\t\t1 m3\t2.350,77 ISK", "Ultraviolet M")]
    [InlineData("Metal Scraps\t1\tCommodities\t0,01 m3\t963,56 ISK", "Metal Scraps")]
    public async Task SingleInventoryRowWithOneSdeCandidate_IsRecordedAsLoot(string text, string name)
    {
        using var env = await Env.StartAsync(sde: SingleRowSde());
        await env.StartRunAsync();

        await env.CopyAsync(text);

        RunLootCapture capture = Assert.Single(await env.CapturesAsync());
        Assert.Equal(name, Assert.Single(capture.Entries).Name);
        Assert.Equal("Loot copied", Assert.Single(env.Toasts.ActionToasts).Title);
    }

    [AvaloniaTheory]
    [InlineData("Use the gate\tthen dock")]
    [InlineData("Annual subscription\t12")]
    [InlineData("Raymond\tback in ten")]
    [InlineData("Product name\t19")]
    [InlineData("KDC-304\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0\t2.71 AU")]
    public async Task SingleInventoryRowWithoutASdeCandidate_IsRejected(string text)
    {
        using var env = await Env.StartAsync(sde: SingleRowSde());
        await env.StartRunAsync();

        await env.CopyAsync(text);

        Assert.Empty(await env.CapturesAsync());
        Assert.Empty(env.Toasts.ActionToasts);
        Assert.Empty(env.Toasts.Toasts);
    }

    [AvaloniaFact]
    public async Task SingleInventoryRowWithMultipleSdeCandidates_IsRejected()
    {
        using var env = await Env.StartAsync(sde: new FakeSdeAccessor()
            .Add(1, "Rifter", 1, 1)
            .Add(2, "Damage Control II", 2, 2));
        await env.StartRunAsync();

        await env.CopyAsync("Rifter\t1\tDamage Control II");

        Assert.Empty(await env.CapturesAsync());
        Assert.Empty(env.Toasts.ActionToasts);
        Assert.Equal("Loot not recognised", Assert.Single(env.Toasts.Toasts).Title);
    }

    [AvaloniaFact]
    public async Task SingleInventoryRowWithUnavailableSde_IsRejectedWithoutGuessing()
    {
        using var env = await Env.StartAsync(sde: SingleRowSde().Offline());
        await env.StartRunAsync();

        await env.CopyAsync("Ultraviolet M\t1\tFrequency Crystal\tMedium\t\t1 m3\t2.350,77 ISK");

        Assert.Empty(await env.CapturesAsync());
        Assert.Empty(env.Toasts.ActionToasts);
        Assert.Equal("Loot not recognised", Assert.Single(env.Toasts.Toasts).Title);
    }

    [AvaloniaFact]
    public async Task InventoryWithKnownEveTypes_OffersLoot_AndSuppressesAnOpenDuplicate()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();
        const string text = "Rifter\t1\r\nDamage Control II\t2";

        await env.CopyAsync(text);
        await env.CopyAsync(text);

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal("Loot copied", offer.Title);
        Assert.Contains("2 EVE item type(s)", offer.Message);
        Assert.Equal(["Exclude", "Close"], Array.ConvertAll(offer.Actions.ToArray(), action => action.Label));

        env.CloseOffer();
        await env.CopyAsync(text);
        Assert.Equal(2, env.Toasts.ActionToasts.Count);
    }

    [Theory]
    [InlineData("Budget rent\t1200\r\nCloud storage\t80")]
    [InlineData("Product name\t19\r\nAnnual subscription\t12")]
    [InlineData("Alice\t1\r\nBob\t2")]
    public async Task InventoryWithoutEveTypes_ExplainsWhyItIsRejected(string text)
    {
        using var env = await Env.StartAsync();

        await env.CopyAsync(text);

        Assert.Empty(env.Toasts.ActionToasts);
        var rejection = Assert.Single(env.Toasts.Toasts);
        Assert.Equal("Loot not recognised", rejection.Title);
        Assert.Contains("None of the 2 copied names is a known item type", rejection.Message);
    }

    [AvaloniaFact]
    public async Task InventoryWithOneKnownType_OffersLootAndNamesUnresolvedRows()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        await env.CopyAsync("Rifter\t1\r\nBudget rent\t2");

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Contains("1 EVE item type(s)", offer.Message);
        Assert.Contains("1 name(s) were not recognised", offer.Message);
    }

    /// <summary>
    /// ET-65, Raymond 2026-09-02: a copied loot window that produced nothing at all — no loot, no toast, no line in
    /// the log, indistinguishable from a watcher that never ran.
    ///
    /// This is a whole loot window whose name column the parser cannot pick out: three item names against two
    /// groups does not clear its 2x distinctness guard, so no column stands out as the names. That refusal used to
    /// leave through the single-row gate, which <c>ResolveUniqueCandidate</c>'s out parameter had overwritten for
    /// every copy regardless of how many rows it held. A refusal the player cannot see is the fault here, not the
    /// refusal: whatever it decides, it has to say so.
    /// </summary>
    [AvaloniaFact]
    public async Task AWholeWindowWhoseNameColumnCannotBeToldApart_IsRefusedOutLoud_NotDropped()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        // Two text columns with the SAME number of distinct values: nothing tells them apart, which is the only
        // case left now that the parser takes the column with strictly the most distinct values.
        await env.CopyAsync("Rifter\tAlpha\t1\r\nDamage Control II\tBeta\t2");

        Assert.Empty(await env.CapturesAsync());
        var refusal = Assert.Single(env.Toasts.Toasts);
        Assert.Equal("Loot not recognised", refusal.Title);
        Assert.Contains("stands out as the item names", refusal.Message);
        // It must not ask for column headings: an EVE inventory copy has none, so that would be unfollowable.
        Assert.DoesNotContain("column shown", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raymond's own loot window, copied verbatim on 2026-09-02 and refused. Seven columns — name, quantity, group,
    /// size, slot, volume, price — with no heading row, empty cells appearing as consecutive tabs, a group name
    /// repeating across rows, and both number conventions on one line (30.229,00 ISK beside 0,10 m3).
    ///
    /// Four distinct item names against three distinct group names did not clear the parser's old 2x distinctness
    /// margin, so the whole window was refused. The margin is gone; the SDE is what catches a wrong column now.
    /// </summary>
    [AvaloniaFact]
    public async Task AWholeLootWindowWithNoHeadingRow_IsRecordedFromItsNameColumn()
    {
        using var env = await Env.StartAsync(sde: new FakeSdeAccessor()
            .Add(1, "Blood Microwave S", 1, 1)
            .Add(2, "Dark Blood Copper Tag", 2, 2)
            .Add(3, "Dark Blood EM Energized Membrane", 3, 3)
            .Add(4, "Gamma S", 1, 1));
        await env.StartRunAsync();

        await env.CopyAsync(
            "Blood Microwave S\t1\tFrequency Crystal\tSmall\t\t1 m3\t30.229,00 ISK\r\n"
            + "Dark Blood Copper Tag\t1\tCriminal Tags\t\t\t0,10 m3\t31.467,72 ISK\r\n"
            + "Dark Blood EM Energized Membrane\t1\tEnergized Armor Membrane\t\tLow\t5 m3\t146.529,04 ISK\r\n"
            + "Gamma S\t1\tFrequency Crystal\tSmall\t\t1 m3\t1.856,16 ISK");

        RunLootCapture capture = Assert.Single(await env.CapturesAsync());
        Assert.Equal(
            ["Blood Microwave S", "Dark Blood Copper Tag", "Dark Blood EM Energized Membrane", "Gamma S"],
            capture.Entries.Select(entry => entry.Name).Order());
        // The comma is the decimal point and the dot groups thousands, on the same row, in both units.
        RunLootEntry crystal = capture.Entries.Single(entry => entry.Name == "Blood Microwave S");
        Assert.Equal(30_229.00m, crystal.ClipboardPrice);
        Assert.Equal(0.10m, capture.Entries.Single(entry => entry.Name == "Dark Blood Copper Tag").Volume);
    }

    [AvaloniaFact]
    public async Task FitCapture_IsNotOfferedAsLoot()
    {
        using var env = await Env.StartAsync();

        await env.CopyAsync("[Rifter, Solo]\r\nDamage Control II");

        Assert.Empty(env.Toasts.ActionToasts);
    }

    /// <summary>The same window copied twice: both captures are kept, the repeat is excluded, and the run is worth
    /// one copy. Silently dropping the repeat and silently adding it are both wrong, so both are asserted.</summary>
    [AvaloniaFact]
    public async Task TheSameWindowCopiedTwice_KeepsBothCaptures_AndCountsOne()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        await env.CopyAsync(Container);
        env.CloseOffer();
        await env.CopyAsync(Container);

        var repeat = Assert.Single(env.Toasts.ActionToasts, toast => toast.Title == "Loot copy repeated");
        Assert.Contains("Identical to the copy at", repeat.Message);
        Assert.Equal(["Include", "Close"], Array.ConvertAll(repeat.Actions.ToArray(), action => action.Label));

        IReadOnlyList<RunLootCapture> captures = await env.CapturesAsync();
        Assert.Equal(2, captures.Count);
        Assert.False(captures[0].IsExcluded);
        Assert.True(captures[1].IsExcluded);
        Assert.Equal(captures[0].ContentHash, captures[1].ContentHash);
        Assert.Equal(2, captures[1].Entries.Count);   // an excluded capture keeps its rows, so it can be put back in

        ActivitySummary summary = await env.SaveAndRebuildAsync();
        Assert.Equal(350.50m, summary.LootIskGained);
        Assert.Equal(3, summary.LootItemCount);
        Assert.Equal(0.30m, summary.LootVolume);
    }

    /// <summary>The toast's own buttons round-trip through the real dispatcher, not just a local flag: "Exclude" on
    /// a fresh capture flips its stored flag, and "Include" on a repeat's card flips it back.</summary>
    [AvaloniaFact]
    public async Task ToastActions_ExcludeAndInclude_RoundTripThroughTheCommand()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        await env.CopyAsync(Container);
        var offer = Assert.Single(env.Toasts.ActionToasts);
        await env.RunActionAsync(offer, "Exclude");
        Assert.True(Assert.Single(await env.CapturesAsync()).IsExcluded);

        env.CloseOffer();
        await env.CopyAsync(Container); // same content again → a repeat, stored excluded by default
        var repeat = Assert.Single(env.Toasts.ActionToasts, toast => toast.Title == "Loot copy repeated");
        await env.RunActionAsync(repeat, "Include");

        IReadOnlyList<RunLootCapture> captures = await env.CapturesAsync();
        Assert.Equal(2, captures.Count);
        Assert.False(captures[1].IsExcluded); // the repeat is back in — the first is untouched, still excluded
    }

    /// <summary>The toast's Exclude/Include button is the same "vanishing exception" risk the fase-2 store path had
    /// (review finding on fase 3): a dispatcher failure must surface as a toast, not disappear off the click.</summary>
    [AvaloniaFact]
    public async Task ExcludeAction_WhenTheCommandFails_ShowsAToastInsteadOfVanishing()
    {
        using var env = await Env.StartAsync(dispatcher => new ThrowingDispatcher(dispatcher,
            command => command is SetRunLootCaptureExclusionCommand));
        await env.StartRunAsync();

        await env.CopyAsync(Container);
        var offer = Assert.Single(env.Toasts.ActionToasts);
        await env.RunActionAsync(offer, "Exclude");

        var failure = Assert.Single(env.Toasts.Toasts, toast => toast.Title == "Loot not updated");
        Assert.Contains("database is locked", failure.Message);
        Assert.False(Assert.Single(await env.CapturesAsync()).IsExcluded); // the failed write never landed
    }

    /// <summary>Two containers in one run read as two different copies, so they are both counted.</summary>
    [AvaloniaFact]
    public async Task TwoDifferentCopies_AreBothIncluded_AndAddUp()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        await env.CopyAsync(Container);
        env.CloseOffer();
        await env.CopyAsync(SecondContainer);

        IReadOnlyList<RunLootCapture> captures = await env.CapturesAsync();
        Assert.Equal(2, captures.Count);
        Assert.All(captures, capture => Assert.False(capture.IsExcluded));

        ActivitySummary summary = await env.SaveAndRebuildAsync();
        Assert.Equal(800.50m, summary.LootIskGained);
        Assert.Equal(108, summary.LootItemCount);
        Assert.Equal(1.80m, summary.LootVolume);
    }

    /// <summary>A row the window showed no price for stays a row without a price, never a zero.</summary>
    [AvaloniaFact]
    public async Task ARowWithoutAPrice_IsVisible_AndMovesNoTotal()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();

        await env.CopyAsync(ContainerWithAPricelessRow);

        RunLootCapture capture = Assert.Single(await env.CapturesAsync());
        Assert.Null(Assert.Single(capture.Entries, entry => entry.Name == "Damage Control II").ClipboardPrice);

        ActivitySummary summary = await env.SaveAndRebuildAsync();
        Assert.Equal(100.00m, summary.LootIskGained);
        Assert.Equal(1, summary.LootEntriesWithoutPrice);
        Assert.Equal(3, summary.LootItemCount);
    }

    /// <summary>Rebuilding reads the exclusions back off the runs, so putting the repeat back in raises the total by
    /// exactly that copy and taking it out again returns it.</summary>
    [AvaloniaFact]
    public async Task RebuildingAfterAnExclusionChanges_FollowsTheFlag()
    {
        using var env = await Env.StartAsync();
        await env.StartRunAsync();
        await env.CopyAsync(Container);
        env.CloseOffer();
        await env.CopyAsync(Container);

        Assert.Equal(350.50m, (await env.SaveAndRebuildAsync()).LootIskGained);
        await env.SetRepeatExcludedAsync(excluded: false);
        Assert.Equal(701.00m, (await env.RebuildAsync()).LootIskGained);
        await env.SetRepeatExcludedAsync(excluded: true);
        Assert.Equal(350.50m, (await env.RebuildAsync()).LootIskGained);
    }

    /// <summary>The toast must not claim success it did not earn (ET-65 phase 3 review finding): without a running
    /// run, nothing is stored and the player is told why, not shown the same "Loot copied" card as a success.</summary>
    [AvaloniaFact]
    public async Task WithoutARunningRun_TheLootIsNotRecorded_AndTheToastSaysSo()
    {
        using var env = await Env.StartAsync();

        await env.CopyAsync(Container);

        Assert.Empty(await env.CapturesAsync());
        Assert.Empty(env.Toasts.ActionToasts);
        var rejection = Assert.Single(env.Toasts.Toasts);
        Assert.Equal("Loot not recorded", rejection.Title);
        Assert.Contains("No run is running", rejection.Message);
    }

    private sealed class Env : IDisposable
    {
        private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly AbyssalLootCapture _capture;
        private readonly FakeClipboardChangeSource _source;

        private Guid _runId;

        public RecordingToastService Toasts { get; } = new();

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source,
            CqrsDispatcher captureDispatcher, FakeSdeAccessor sde)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _capture = new AbyssalLootCapture(watch, Toasts, sde, NullLogger<AbyssalLootCapture>.Instance, captureDispatcher);
        }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        /// <param name="wrapDispatcher">Lets a test intercept the dispatcher calls <see cref="AbyssalLootCapture"/>
        /// itself makes (e.g. to fail a specific command), without touching the real one this Env's own helpers
        /// (StartRunAsync, CapturesAsync, ...) use.</param>
        public static async Task<Env> StartAsync(Func<CqrsDispatcher, CqrsDispatcher>? wrapDispatcher = null,
            FakeSdeAccessor? sde = null)
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create();
            var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
                NullLogger<ClipboardWatchService>.Instance, source);
            var realDispatcher = instance.Services.GetRequiredService<CqrsDispatcher>();
            var env = new Env(instance, watch, source, wrapDispatcher?.Invoke(realDispatcher) ?? realDispatcher,
                sde ?? FakeSdeAccessor.WithSampleFit());
            await watch.SetEnabledAsync(true);
            return env;
        }

        public async Task CopyAsync(string text)
        {
            _source.ClipboardText = text;
            _source.RaiseChanged();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await _capture.LastStore;
        }

        /// <summary>Copying the same text again only asks a second time once the first card is gone.</summary>
        public void CloseOffer() => Toasts.ActionToasts[^1].Actions.First(action => action.Label == "Close").Run();

        /// <summary>Runs one of a shown toast's buttons and waits for whatever dispatcher call it made.</summary>
        public async Task RunActionAsync(
            (string Title, string? Message, ToastKind Kind, IReadOnlyList<ToastAction> Actions, string? ReplacementKey) toast,
            string label)
        {
            toast.Actions.First(action => action.Label == label).Run();
            await _capture.LastStore;
        }

        public async Task StartRunAsync()
        {
            Result<Guid> started = await Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
                1234, "Abyssal Deadspace", 30000142));
            Assert.True(started.IsSuccess);
            _runId = started.Value;
        }

        public async Task<IReadOnlyList<RunLootCapture>> CapturesAsync()
        {
            await using ClientDbContext db = await CreateDbAsync();
            return await db.Set<RunLootCapture>()
                .AsNoTracking()
                .Include(capture => capture.Entries)
                .OrderBy(capture => capture.CapturedAtUtc)
                .ToListAsync(Token);
        }

        public async Task<ActivitySummary> SaveAndRebuildAsync()
        {
            Result saved = await Send(new SaveRunCommand(_runId, StartedAtUtc.AddMinutes(15),
                StartedAtUtc.AddMinutes(16), [], [], [], []));
            Assert.True(saved.IsSuccess);
            return await RebuildAsync();
        }

        public async Task<ActivitySummary> RebuildAsync()
        {
            Result<int> rebuilt = await Send(new RebuildActivitySummariesCommand());
            Assert.True(rebuilt.IsSuccess);
            await using ClientDbContext db = await CreateDbAsync();
            return Assert.Single(await db.Set<ActivitySummary>().AsNoTracking().ToListAsync(Token));
        }

        /// <summary>Stands in for the one click that phase 3 adds.</summary>
        public async Task SetRepeatExcludedAsync(bool excluded)
        {
            await using ClientDbContext db = await CreateDbAsync();
            List<RunLootCapture> captures = await db.Set<RunLootCapture>()
                .OrderBy(capture => capture.CapturedAtUtc)
                .ToListAsync(Token);
            captures[^1].IsExcluded = excluded;
            await db.SaveChangesAsync(Token);
        }

        public void Dispose()
        {
            _capture.Dispose();
            _watch.Dispose();
            _instance.Dispose();
        }

        private Task<TResult> Send<TResult>(ICommand<TResult> command) =>
            _instance.Services.GetRequiredService<CqrsDispatcher>().Send(command, Token);

        private Task<ClientDbContext> CreateDbAsync() =>
            _instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
    }

    private static FakeSdeAccessor SingleRowSde() => new FakeSdeAccessor()
        .Add(1, "Ultraviolet M", 1, 1)
        .Add(2, "Metal Scraps", 2, 2);

    /// <summary>Fails one specific command the way a locked database would — an exception out of the dispatcher —
    /// so a test can prove that path is caught and shown, not swallowed.</summary>
    private sealed class ThrowingDispatcher(CqrsDispatcher inner, Func<object, bool> shouldThrow) : CqrsDispatcher
    {
        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            inner.Query(query, cancellationToken);

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            inner.Send(command, cancellationToken);

        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) =>
            shouldThrow(command)
                ? throw new InvalidOperationException("database is locked")
                : inner.Send(command, cancellationToken);
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
