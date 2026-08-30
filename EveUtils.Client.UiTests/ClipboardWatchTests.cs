using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The clipboard system (ET-57): which shapes it recognises, and the promise that everything else leaves the
/// process without reaching a subscriber. The Win32 change notification itself is thin platform plumbing and is
/// substituted here; the parts worth pinning are the recognition and the routing around it.
///
/// The fit and inventory samples are taken verbatim from clipboard captures out of a running EVE client (two fit
/// exports, plus the details, list and icons views of an item hangar), so the shapes below are what the game
/// actually writes rather than what the format is assumed to look like.
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
    // Ordinary things people copy all day.
    [InlineData("correct horse battery staple", ClipboardShape.Unrecognised)]
    [InlineData("https://example.test/some/page", ClipboardShape.Unrecognised)]
    [InlineData("first line\r\nsecond line\r\nthird line", ClipboardShape.Unrecognised)]
    [InlineData("   \r\n  \r\n ", ClipboardShape.Unrecognised)]
    [InlineData(null, ClipboardShape.Unrecognised)]
    public void Recognise_AcceptsFitsAndInventoryTables_AndNothingElse(string? text, ClipboardShape expected) =>
        Assert.Equal(expected, ClipboardShapeRecogniser.Recognise(text));

    /// <summary>
    /// The guarantee the feature is sold on: an unrecognised payload never reaches a subscriber, a recognised one
    /// reaches every current subscriber, and unsubscribing removes the feature from the disclosure as well as from
    /// the delivery.
    /// </summary>
    [AvaloniaFact]
    public void ClipboardWatch_DeliversRecognisedPayloadsOnly_ToItsCurrentSubscribers()
    {
        var source = new FakeClipboardChangeSource();
        var dialogs = new RecordingDialogService();
        using var empty = new ServiceCollection().BuildServiceProvider();
        using var watch = new ClipboardWatchService(dialogs, empty, NullLogger<ClipboardWatchService>.Instance, source);

        var delivered = new List<ClipboardCapture>();
        var subscription = watch.Subscribe("Abyssal run loot", delivered.Add);
        Assert.Equal(["Abyssal run loot"], watch.Consumers);

        dialogs.ClipboardText = "correct horse battery staple";
        Copy(source);
        Assert.Empty(delivered);

        dialogs.ClipboardText = "[Gila, Abyssal T5]\n\nDrone Damage Amplifier II";
        Copy(source);
        Assert.Equal(ClipboardShape.Fit, Assert.Single(delivered).Shape);

        subscription.Dispose();
        Assert.Empty(watch.Consumers);

        Copy(source);
        Assert.Single(delivered);
    }

    // The notification arrives off the UI thread and the read is marshalled onto it; run the posted work.
    private static void Copy(FakeClipboardChangeSource source)
    {
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public bool IsSupported => true;

        public event Action? Changed;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public void RaiseChanged() => Changed?.Invoke();
    }
}
