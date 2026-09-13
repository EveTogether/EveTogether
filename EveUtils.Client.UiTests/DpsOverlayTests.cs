using System;
using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Controls;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless render check for the per-character DPS overlay: feeds a live tracker, renders the real
/// borderless overlay window with the smoothed graph, asserts it produces real pixels and saves a PNG so the
/// result can be eyeballed without launching the app.
/// </summary>
public class DpsOverlayTests
{
    [AvaloniaFact]
    public void DpsOverlay_Renders_SmoothedGraph_WithLiveData()
    {
        var tracker = new DpsViewModel("Jithran", isSelf: true);

        // A varied ramp so the smoothed curve has real shape (mirrors the synthetic feeder pattern).
        int[] dealt = [0, 120, 300, 520, 760, 880, 900, 840, 600, 320, 180, 260, 540, 820, 910, 760, 420, 200, 90, 40];
        int[] received = [10, 40, 80, 60, 120, 90, 40, 20, 60, 110, 70, 30, 50, 90, 40, 20, 80, 110, 60, 20];
        for (var i = 0; i < dealt.Length; i++)
            tracker.Apply(new DpsSampleDto(91000000, "Jithran", dealt[i], received[i], DateTimeOffset.UtcNow));

        Assert.Equal(40, tracker.Dealt);                 // last dealt sample is reflected
        Assert.Equal(20, tracker.Received);              // last received sample is reflected
        Assert.True(tracker.GraphRevision >= dealt.Length);

        var window = new DpsOverlayWindow(tracker) { Width = 460, Height = 260 };
        window.Show();
        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal(460, frame!.PixelSize.Width);
        Assert.Equal(260, frame.PixelSize.Height);
        frame.Save("/tmp/eveutils-dps-overlay.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void DpsOverlay_ShowsBountyAndLocation_WhenKnown()
    {
        var tracker = new DpsViewModel("Jithran", isSelf: true) { Location = "Irnin", Bounty = 894_400 };

        // The shared VM exposes the same formatted/guard properties the fleet-metrics row binds to.
        Assert.True(tracker.HasBounty);
        Assert.Equal("894.4k ISK", tracker.BountyText);

        var window = new DpsOverlayWindow(tracker) { Width = 460, Height = 260 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-dps-overlay-bounty-location.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void DpsOverlay_Graph_UpdatesLive_AfterShow()
    {
        var tracker = new DpsViewModel("Jithran", isSelf: false);
        var window = new DpsOverlayWindow(tracker) { Width = 460, Height = 260 };
        window.Show();
        window.CaptureRenderedFrame(); // initial (empty) render

        // Feed samples AFTER Show — mirrors real combat arriving while the overlay is already open.
        int[] dealt = [0, 150, 420, 700, 880, 910, 840, 560, 300, 180, 260, 560, 820, 900, 720, 400, 200, 90, 40, 20];
        for (var i = 0; i < dealt.Length; i++)
            tracker.Apply(new DpsSampleDto(91000000, "Jithran", dealt[i], 0, DateTimeOffset.UtcNow));

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var after = window.CaptureRenderedFrame();

        Assert.NotNull(after);
        Assert.Equal(20, tracker.Dealt);
        after!.Save("/tmp/eveutils-dps-overlay-live.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void RenderFrame_LocalCharacter_WithNoCombatYet_StillScrolls()
    {
        var tracker = new DpsViewModel("Jithran", isSelf: true);
        // A local character samples zero rates until its first hit; the graph must still advance each frame (scroll
        // flat) like a fleet meter, not freeze — the regression that left a character-list pop-out completely dead.
        tracker.UseSampler(() => new CombatRates(0, 0, 0, 0, 0, 0, 0, 0));

        var before = tracker.GraphRevision;
        tracker.RenderFrame();
        tracker.RenderFrame();

        Assert.True(tracker.GraphRevision > before);
    }

    [AvaloniaFact]
    public void RenderFrame_RemoteCharacter_NullSampler_DefersToApply()
    {
        var tracker = new DpsViewModel("Remote", isSelf: false);
        // A purely remote member returns null so its event-driven Apply path owns the series; the render frame must
        // not advance (and overwrite) it.
        tracker.UseSampler(() => null);

        var before = tracker.GraphRevision;
        tracker.RenderFrame();

        Assert.Equal(before, tracker.GraphRevision);
    }

    /// <summary>
    /// A fleet member's incoming-rep sample must reach its graph line the same way neut/cap already do — through
    /// <see cref="DpsViewModel.SetRate"/>'s generic extra-line lookup, with no special case needed. A kind that is
    /// emitted but never wired to a line here would fail silently: no exception, the figure just never moves.
    /// </summary>
    [AvaloniaFact]
    public void SetRate_RepIn_MovesTheRepInLine()
    {
        var tracker = new DpsViewModel("Remote", isSelf: false);
        tracker.SetRate(MetricKind.RepIn, 250);

        for (var i = 0; i < 20; i++)
            tracker.RenderFrame();

        // The one line that moved is the reps-received line: solid (received), rep ink, hp/s lane (ET-277).
        var moved = Assert.Single(tracker.Series, series => series.Values.Count > 0 && series.Values[^1] > 0);
        Assert.Same(CombatInk.Rep, moved.Stroke);
        Assert.Equal(GraphLane.HitPoints, moved.Lane);
        Assert.False(moved.Dashed);
        Assert.Equal(250, tracker.RepIn);
    }

    [AvaloniaFact]
    public void FleetCard_TheFigure_ReadsWhereTheLineEnds_WhileTheRateFalls()
    {
        // A fleet card is fed once a second and its line averages that over the last frames. As a HAM fight wound down
        // on 13 Sep a card read OUT 124 beside a line still ending near 250 (ET-282).
        var meter = new DpsViewModel("Jithran", isSelf: false);
        meter.SetRate(MetricKind.Dps, 397);
        for (var i = 0; i < 60; i++)
            meter.RenderFrame();
        meter.SetRate(MetricKind.Dps, 124);
        for (var i = 0; i < 10; i++)
            meter.RenderFrame();

        var outLine = meter.Series.Single(series => ReferenceEquals(series.Stroke, CombatInk.Out));
        Assert.InRange(outLine.Values[^1], 125, 396);           // the line on its way down…
        Assert.Equal((long)outLine.Values[^1], meter.Dealt);    // …and the figure beside it saying the same
    }

    [AvaloniaFact]
    public void DpsOverlay_EmaSmoothing_RoundsStepEdges()
    {
        var tracker = new DpsViewModel("Jithran", isSelf: true);
        var window = new DpsOverlayWindow(tracker) { Width = 560, Height = 280 };
        window.Show();

        // Drive it like the ~30fps render timer: idle, a hard step up to 800, plateau, then a hard step down. The EMA
        // must round both edges of the LINE (demo-parity) instead of vertical cliffs.
        void Feed(long dps, int frames)
        {
            for (var i = 0; i < frames; i++)
                tracker.ApplyRates(new CombatRates(dps, 0, 0, 0, 0, 0, 0, 0));
        }

        Feed(0, 40);
        Feed(800, 220);
        Feed(0, 5);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        var outLine = tracker.Series.Single(series => ReferenceEquals(series.Stroke, CombatInk.Out));
        Assert.InRange(outLine.Values[^1], 1, 799); // the line still trailing the step-down, not snapped to 0…
        // …and the number reads where the line ends (ET-282). It used to show the rate already at 0 beside a line
        // still at 667, which on screen read as two different figures.
        Assert.Equal((long)outLine.Values[^1], tracker.Dealt);
        Assert.Equal(Math.Round(outLine.Values[^1] / tracker.HitPointsScale * 200) / 200, tracker.OutFraction);
        frame!.Save("/tmp/eveutils-dps-overlay-ema.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }
}
