using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The clipboard system (ET-57): which shapes it recognises, and the promise that everything else leaves the
/// process without reaching a subscriber; the Win32 change notification itself is thin platform plumbing,
/// substituted here so the recognition and routing are what is pinned. Fit and inventory samples are taken
/// verbatim from a running EVE client; the ET-79 signature samples are constructed from the vermoeden format in
/// docs/clipboard.md §7, not yet from a live capture.
/// </summary>
public class ClipboardWatchTests
{
    [Theory]
    // Fit headers copied out of the game's own fit export, verbatim.
    [InlineData("[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II\r\n\r\nMultispectrum Shield Hardener II", ClipboardShape.Fit)]
    [InlineData("[Armageddon, PVE High DPS 0-60KM (1000+)]\r\nBallistic Control System II", ClipboardShape.Fit)]
    [InlineData("[Rifter, Solo]", ClipboardShape.Fit)]
    [InlineData("  \r\n[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II", ClipboardShape.Fit)]
    // Bracketed but not a fit header. IFitTextImporter.Detect accepts all of these as EFT, which is right for a
    // paste window and wrong for a hook that sees every copy of the day.
    [InlineData("[WARNING] disk almost full\r\n[WARNING] disk almost full", ClipboardShape.Unrecognised)]
    [InlineData("[see the docs](https://example.test/page)", ClipboardShape.Unrecognised)]
    [InlineData("[TODO] buy milk, bread", ClipboardShape.Unrecognised)]
    // Inventory, details view: seven fields, empty ones in the middle and at the end, quantities and prices in the
    // player's own locale (a dot groups thousands here, a comma is the decimal point).
    [InlineData("Agitated Exotic Filament\t1\tAbyssal Filaments\t\t\t0,10 m3\t42.237,65 ISK\r\n" +
                "Baryon Exotic Plasma S Blueprint\t\tExotic Plasma Charge Blueprint\t\t\t0,01 m3\t", ClipboardShape.Inventory)]
    // Inventory, icons view: only two fields, and the second one is often empty. A "needs several columns" rule
    // would throw this away, so the column count cannot be what tells an inventory from other tab-separated text.
    [InlineData("Entropic Radiation Sink I Blueprint\t\r\nTriglavian Survey Database\t682", ClipboardShape.Inventory)]
    // A single stack is one row, so it stays unrecognised: one tabbed line is not a table (known limit, ET-57).
    [InlineData("Triglavian Survey Database\t682", ClipboardShape.Unrecognised)]
    // Ragged rows are not a table either — this is what rules out most pasted prose that happens to hold a tab.
    [InlineData("Agitated Exotic Filament\t1\tAbyssal Filaments\r\nBaryon Exotic Plasma S Blueprint\t", ClipboardShape.Unrecognised)]
    // Signature: a single copied signature is enough on its own (ET-79 AC-1).
    [InlineData("KDC-304\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU", ClipboardShape.Signature)]
    // Several signature rows also carry an equal tab count per row — exactly what IsInventoryTable alone would
    // accept — so the signature check has to run first, or a scan-window copy is misdelivered (ET-79 AC-2).
    [InlineData("KDC-304\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                "ABC-123\tCosmic Anomaly\t\t\t25.0%\t-\r\n" +
                "XYZ-789\tCosmic Signature\t\t\t50.0%\t5,17 AU", ClipboardShape.Signature)]
    // No word from the EVE UI is an anchor: a translated kind/group and a comma-decimal percentage still
    // recognise (ET-79 AC-3) — this stands in for a non-English client capture until one is measured (§7).
    [InlineData("KDC-304\t中庭の亡霊\t戦闘サイト\t中庭の亡霊\t100,00%\t5,17 AE", ClipboardShape.Signature)]
    // Six fields but no percentage on the fifth is not a signature row — the anchor is load-bearing.
    [InlineData("KDC-304\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0\t2.71 AU", ClipboardShape.Unrecognised)]
    // Raymond's own repro (2026-09-01): two copies of a Sansha Hideaway, one after another, verbatim including the
    // comma-decimal scan percentage his client writes. Both recognise identically — the "no toast" bug is not here.
    [InlineData("VQX-959\tCosmic Anomaly\tCombat Site\tSansha Hideaway\t100,0%\t10,93 AU", ClipboardShape.Signature)]
    [InlineData("FOY-540\tCosmic Anomaly\tCombat Site\tSansha Hideaway\t100,0%\t9,79 AU", ClipboardShape.Signature)]
    // Ordinary things people copy all day.
    [InlineData("correct horse battery staple", ClipboardShape.Unrecognised)]
    [InlineData("https://example.test/some/page", ClipboardShape.Unrecognised)]
    [InlineData("first line\r\nsecond line\r\nthird line", ClipboardShape.Unrecognised)]
    [InlineData("   \r\n  \r\n ", ClipboardShape.Unrecognised)]
    [InlineData(null, ClipboardShape.Unrecognised)]
    public void Recognise_AcceptsFitsAndInventoryTables_AndNothingElse(string? text, ClipboardShape expected) =>
        Assert.Equal(expected, ClipboardShapeRecogniser.Recognise(text));

    /// <summary>
    /// The guarantees the feature is sold on, in the order a sceptical user would ask about them: nothing is read
    /// while the watcher is off, nothing is read while no feature is listening, an unrecognised payload never
    /// reaches a subscriber, a recognised one reaches every current subscriber, and switching off stops the reading
    /// even for a notification that was already on its way.
    /// </summary>
    [AvaloniaFact]
    public async Task ClipboardWatch_ReadsTheClipboardOnlyWhileSwitchedOn_AndListenedTo()
    {
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);

        // Off is the state after an install: a copy is not read at all.
        Copy(source);
        Assert.Equal(0, dialogs.ClipboardReads);

        await watch.SetEnabledAsync(true);
        Assert.True(watch.IsWatching);

        // On, but nothing subscribed yet — still not read, so the disclosure's "nothing uses this" is literal.
        Copy(source);
        Assert.Equal(0, dialogs.ClipboardReads);

        var delivered = new List<ClipboardCapture>();
        var subscription = watch.Subscribe("Abyssal run loot", delivered.Add);
        Assert.Equal(["Abyssal run loot"], watch.Consumers);

        dialogs.ClipboardText = "correct horse battery staple";
        Copy(source);
        Assert.Equal(1, dialogs.ClipboardReads);
        Assert.Empty(delivered);

        dialogs.ClipboardText = "[Jackdaw, Jackdaw - T1/T2 - D]\r\nBallistic Control System II";
        Copy(source);
        Assert.Equal(ClipboardShape.Fit, Assert.Single(delivered).Shape);

        // Switched off with a subscriber still registered: the notification arrives, nothing is read.
        await watch.SetEnabledAsync(false);
        Assert.False(watch.IsWatching);
        var readsBeforeOff = dialogs.ClipboardReads;
        Copy(source);
        Assert.Equal(readsBeforeOff, dialogs.ClipboardReads);
        Assert.Single(delivered);

        subscription.Dispose();
        Assert.Empty(watch.Consumers);
    }

    /// <summary>
    /// wl-paste reports the clipboard that was already there the moment it starts, and that copy was made before
    /// the user switched watching on — so the Wayland source drops it and reports every change after it.
    /// </summary>
    /// <remarks>
    /// Driven through a reader rather than a real <c>wl-paste</c>: the process needs a Wayland compositor and the
    /// build agents have none, while the rule being pinned lives in the lines, not in the process.
    /// </remarks>
    [Fact]
    public void WaylandSource_DropsTheStartupLine_AndReportsEveryChangeAfterIt()
    {
        var source = new WaylandClipboardChangeSource();
        var changes = 0;
        source.Changed += () => changes++;

        // Four lines out of wl-paste: the startup one, then three copies the user actually made.
        source.Pump(new StringReader("\n\n\n\n"));

        Assert.Equal(3, changes);
    }

    /// <summary>
    /// A desktop that cannot notify makes wl-paste exit without ever writing a line, so a source that gets no
    /// first line reports itself unsupported instead of sitting there looking started.
    /// </summary>
    [Fact]
    public void WaylandSource_ReportsUnsupported_WhenNoLineEverArrives()
    {
        var source = new WaylandClipboardChangeSource();
        var reported = 0;
        source.SupportChanged += () => reported++;

        source.Pump(new StringReader(""));

        Assert.False(source.IsSupported);
        Assert.Equal(1, reported);
    }

    /// <summary>
    /// A watcher that falls away mid-run ends the pump, and that end is reported rather than silent — otherwise
    /// the switch goes on looking on while nothing will ever arrive again.
    /// </summary>
    [Fact]
    public void WaylandSource_ReportsTheEndOfThePump_WhenTheWatcherFallsAway()
    {
        var source = new WaylandClipboardChangeSource();
        var changes = 0;
        var reported = 0;
        source.Changed += () => changes++;
        source.SupportChanged += () => reported++;

        // The startup line, one copy the user made, and then the watcher is gone.
        source.Pump(new StringReader("\n\n"));

        Assert.Equal(1, changes);
        Assert.Equal(1, reported);
    }

    /// <summary>
    /// A source that loses its notifier while running — a compositor restart, a killed helper — stops the watch
    /// rather than leaving the status line claiming the clipboard is still being read.
    /// </summary>
    [AvaloniaFact]
    public async Task ClipboardWatch_StopsClaimingToWatch_WhenTheSourceLosesItsNotifier()
    {
        var source = new FakeClipboardChangeSource();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);

        await watch.SetEnabledAsync(true);
        Assert.True(watch.IsWatching);

        var stateChanges = 0;
        watch.StateChanged += () => stateChanges++;

        source.RaiseSupportChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.False(watch.IsWatching);
        Assert.Equal(1, stateChanges);
    }

    /// <summary>
    /// Where a platform reads the clipboard over its own channel, that reading is the one used — the toplevel is not
    /// consulted at all. On Wayland the two disagree, and the toplevel's X11 selection is the one that comes back empty.
    /// </summary>
    [AvaloniaFact]
    public async Task WhereTheSourceReadsTheClipboardItself_TheToplevelIsNotAsked()
    {
        var source = new FakeClipboardChangeSource { OwnText = "[Jackdaw, from the platform]\r\nBallistic Control System II" };
        var dialogs = new RecordingDialogService { ClipboardText = "[Rifter, from the toplevel]\r\nGyrostabilizer II" };
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(dialogs, instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source);
        await watch.SetEnabledAsync(true);

        var delivered = new List<ClipboardCapture>();
        using var subscription = watch.Subscribe("Test", delivered.Add);
        Copy(source);

        Assert.Contains("from the platform", Assert.Single(delivered).Text);
        Assert.Equal(0, dialogs.ClipboardReads);
    }

    // The notification arrives off the UI thread and the read is marshalled onto it; run the posted work.
    private static void Copy(FakeClipboardChangeSource source)
    {
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        /// <summary>What this source reads over its own channel; null leaves the reading to the toplevel.</summary>
        public string? OwnText { get; init; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(OwnText);

        public void RaiseChanged() => Changed?.Invoke();

        public void RaiseSupportChanged() => SupportChanged?.Invoke();
    }
}
