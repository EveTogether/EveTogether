using Avalonia;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Controls.Map;
using EveUtils.Shared.Modules.Map.Dtos;
using Xunit;
using static EveUtils.Client.UiTests.MapFollowFleetTests;

namespace EveUtils.Client.UiTests;

/// <summary>ET-409: Follow fleet fills about half the view with the group, so the systems around it stay in sight, and a
/// group in one system gets a fixed zoom rather than the deepest one. Measured on the real k-space graph in the Fleet metrics
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
        Assert.Equal(40, Zoom(graph, [jita], view, fit), 2);
        Assert.InRange(Fill(graph, [jita, world.IndexOf(Perimeter)], view, fit), 0.45, 0.5);
        Assert.InRange(Fill(graph, [.. chain], view, fit), 0.45, 0.5);
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