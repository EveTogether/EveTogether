using Avalonia;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Controls.Map;
using EveUtils.Shared.Modules.Map.Dtos;
using Xunit;
using static EveUtils.Client.UiTests.MapFollowFleetTests;

namespace EveUtils.Client.UiTests;

/// <summary>ET-409: Follow fleet fills about half the view with the group, so the systems around it stay in sight, and a
/// group in one system gets the deepest zoom of all, which no larger group goes past. Measured on the real k-space graph in the Fleet metrics
/// card's view (438×395).</summary>
public sealed class MapFleetZoomTests
{
    [AvaloniaFact]
    public async Task TheGroupFillsAboutHalfTheView_AtOneSystemTwoNeighboursAndFiveJumps()
    {
        using var world = await World.OpenAsync();
        world.Show();
        MapGraphDto graph = world.Model.Graph!;
        int jita = world.IndexOf(Jita);
        var depth = new Dictionary<int, int> { [jita] = 0 };
        var parent = new Dictionary<int, int>();
        var queue = new Queue<int>([jita]);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (int next in graph.NeighboursOf(at).ToArray().Where(next => !depth.ContainsKey(next)))
            {
                depth[next] = depth[at] + 1;
                parent[next] = at;
                queue.Enqueue(next);
            }
        }

        int far = depth.Where(pair => pair.Value == 5).Select(pair => pair.Key).First();
        List<int> chain = [far];
        while (chain[^1] != jita)
            chain.Add(parent[chain[^1]]);

        var view = new Size(438, 395);
        double fit = Math.Min(view.Width / graph.Width, view.Height / graph.Height) * 0.92;
        double one = Zoom(graph, [jita], view, fit), two = Zoom(graph, [jita, world.IndexOf(Perimeter)], view, fit), five = Zoom(graph, [.. chain], view, fit);
        Assert.Equal(50, one, 2);
        Assert.True(two <= one, $"two neighbours zoom {two:0.0}×, deeper than one system at {one:0.0}×");
        Assert.True(five <= two, $"five jumps zoom {five:0.0}×, deeper than two neighbours at {two:0.0}×");
        Assert.InRange(Fill(graph, [.. chain], view, fit), 0.45, 0.5);
    }

    [Fact]
    public void TheZoom_NeverGrowsWithTheSpread_FromOneSystemOutToTheWholeMap()
    {
        var view = new Size(438, 395);
        const double fit = 0.03;
        double previous = double.MaxValue;
        foreach (double span in new[] { 0, 1, 5, 20, 57, 120, 170, 400, 1000, 5000, 10000 })
        {
            double zoom = StarMapControl.FleetFrameScale(span, span / 2, view, fit) / fit;
            Assert.True(zoom <= previous, $"a spread of {span} zooms to {zoom:0.0}×, deeper than the smaller one at {previous:0.0}×");
            Assert.InRange(zoom, 1, 50);
            previous = zoom;
        }
    }

    private static double Zoom(MapGraphDto graph, int[] set, Size view, double fit) => _Scale(graph, set, view, fit) / fit;

    // How much of the tighter axis of the view the group covers.
    private static double Fill(MapGraphDto graph, int[] set, Size view, double fit)
    {
        double scale = _Scale(graph, set, view, fit);
        return Math.Max((set.Max(i => graph.Systems[i].X) - set.Min(i => graph.Systems[i].X)) * scale / view.Width,
                        (set.Max(i => graph.Systems[i].Y) - set.Min(i => graph.Systems[i].Y)) * scale / view.Height);
    }

    private static double _Scale(MapGraphDto graph, int[] set, Size view, double fit) =>
        StarMapControl.FleetFrameScale(set.Max(i => graph.Systems[i].X) - set.Min(i => graph.Systems[i].X),
            set.Max(i => graph.Systems[i].Y) - set.Min(i => graph.Systems[i].Y), view, fit);
}