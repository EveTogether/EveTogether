using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.Views;
using EveUtils.Client.WorldMap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417: with OPSEC on, no map is drawn anywhere — the MAP tab and its pop-out (one view, MapWindow) and a
/// fleet's map card — and "Hidden by OPSEC" stands in its place. Masking the names would not do: the shape of the map
/// around a marker is a location on its own.</summary>
public sealed class OpsecMapTests
{
    [AvaloniaFact]
    public async Task MapWindow_OpsecOn_DrawsNoMap_AndSaysWhy()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<IOpsecService>().SetEnabledAsync(true);
        using MapViewModel map = instance.Services.GetRequiredService<IMapLauncher>().Create();

        var window = new MapWindow(map) { Width = 1180, Height = 720 };
        window.Show();

        Assert.False(_Map(window, "Map").IsEffectivelyVisible);
        Assert.True(_Placeholder(window).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MapWindow_Toggle_HidesAndShowsTheMapLive()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        var opsec = instance.Services.GetRequiredService<IOpsecService>();
        using MapViewModel map = instance.Services.GetRequiredService<IMapLauncher>().Create();
        var window = new MapWindow(map) { Width = 1180, Height = 720 };
        window.Show();
        Assert.True(_Map(window, "Map").IsEffectivelyVisible);

        await opsec.SetEnabledAsync(true);
        Assert.False(_Map(window, "Map").IsEffectivelyVisible);
        Assert.True(_Placeholder(window).IsEffectivelyVisible);

        await opsec.SetEnabledAsync(false);
        Assert.True(_Map(window, "Map").IsEffectivelyVisible);
        Assert.False(_Placeholder(window).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FleetMapCard_OpsecOn_DrawsNoMap_AndSaysWhy()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<IOpsecService>().SetEnabledAsync(true);
        using MapViewModel map = instance.Services.GetRequiredService<IMapLauncher>().Create();

        var card = new FleetMapCard { DataContext = map };
        var host = new Window { Width = 440, Height = 560, Content = card };
        host.Show();

        Assert.False(_Map(card, "CardMap").IsEffectivelyVisible);
        Assert.True(_Placeholder(card).IsEffectivelyVisible);
        host.Close();
    }

    [AvaloniaFact]
    public async Task MapViewModel_Disposed_StopsFollowingOpsec()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        var opsec = instance.Services.GetRequiredService<IOpsecService>();
        MapViewModel map = instance.Services.GetRequiredService<IMapLauncher>().Create();

        map.Dispose();
        await opsec.SetEnabledAsync(true);

        Assert.False(map.IsHiddenByOpsec);
    }

    private static StarMapControl _Map(Control root, string name) =>
        root.FindControl<StarMapControl>(name) ?? throw new InvalidOperationException($"no {name}");

    private static OpsecMapPlaceholder _Placeholder(Visual root) =>
        root.GetVisualDescendants().OfType<OpsecMapPlaceholder>().Single();
}
