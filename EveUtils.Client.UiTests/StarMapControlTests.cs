using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveUtils.Client.Controls.Map;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Graph;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-392: the map control on the real k-space map, driven by pointer input the way a pilot drives it.</summary>
public sealed class StarMapControlTests
{
    private const int Width = 1400;
    private const int Height = 900;

    private static readonly Lazy<MapGraphDto> Graph = new(() =>
        MapGraphBuilder.Build(MapFixture.Snapshot, MapFixture.BuildNumber, id => MapFixture.Factions.GetValueOrDefault(id))
        ?? throw new InvalidOperationException("the fixture holds no map"));

    /// <summary>The level of detail follows the zoom: regions below 2.6×, constellations below 7×, systems above.
    /// One wheel notch is ×1.25, so 4 notches is 2.4×, 5 is 3.1×, 8 is 6.0×, 9 is 7.5×.</summary>
    [AvaloniaTheory]
    [InlineData(0, MapDetailLevel.Regions)]
    [InlineData(4, MapDetailLevel.Regions)]
    [InlineData(5, MapDetailLevel.Constellations)]
    [InlineData(8, MapDetailLevel.Constellations)]
    [InlineData(9, MapDetailLevel.Systems)]
    public void WheelZoom_SwitchesTheLevelOfDetailAtTheThresholds(int notches, MapDetailLevel expected)
    {
        (Window window, StarMapControl map) = _Show();

        window.MouseWheel(new Point(Width / 2.0, Height / 2.0), new Vector(0, notches));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expected, map.DetailLevel);
    }

    /// <summary>FIND flies to the system at system zoom and puts it in the middle, where a click then selects it —
    /// the spatial grid finding the system under the pointer.</summary>
    [AvaloniaFact]
    public void FocusRequest_ThenClickInTheMiddle_SelectsThatSystem()
    {
        (Window window, StarMapControl map) = _Show();
        Graph.Value.TryGetIndex(30000142, out int jita);

        map.FocusRequest = new MapFocusRequest([jita]);
        _Settle();
        window.MouseDown(new Point(Width / 2.0, Height / 2.0), MouseButton.Left);
        window.MouseUp(new Point(Width / 2.0, Height / 2.0), MouseButton.Left);

        Assert.Equal(MapDetailLevel.Systems, map.DetailLevel);
        Assert.Equal(jita, map.SelectedIndex);
    }

    /// <summary>ET-392 acceptance: a full frame with all ~7k jump lines, drawn to a real Skia surface, at each level of
    /// detail. The figure is reported; the bound only catches a return to per-frame geometry (tens of ms).</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void FullFrame_WithEveryJumpLine_RendersWithinAFrameBudget(int notches)
    {
        (Window window, StarMapControl map) = _Show();
        window.MouseWheel(new Point(Width / 2.0, Height / 2.0), new Vector(0, notches));
        Dispatcher.UIThread.RunJobs();
        using var surface = new RenderTargetBitmap(new PixelSize(Width, Height));
        surface.Render(map);

        var clock = Stopwatch.StartNew();
        const int frames = 20;
        foreach (int _ in Enumerable.Range(0, frames))
            surface.Render(map);
        double perFrame = clock.Elapsed.TotalMilliseconds / frames;

        TestContext.Current.TestOutputHelper?.WriteLine($"{map.DetailLevel}: {perFrame:0.0} ms per full frame ({Graph.Value.Jumps.Count} jumps)");
        Assert.True(perFrame < 33, $"{perFrame:0.0} ms per frame at {map.DetailLevel}");
    }

    private static (Window, StarMapControl) _Show()
    {
        var map = new StarMapControl { Graph = Graph.Value };
        var window = new Window { Width = Width, Height = Height, Content = map };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return (window, map);
    }

    // A flight is animated on the render clock; tick it through well past its 450 ms.
    private static void _Settle()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 700)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(16);
        }
    }
}
