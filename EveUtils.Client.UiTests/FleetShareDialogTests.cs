using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The per-fleet sharing dialog (per-(fleet, character, metric) override + "apply to all my characters"). Asserts the
/// override writes it produces and that the window renders headless.
/// </summary>
public class FleetShareDialogTests
{
    private const long FleetId = 77;

    [Fact]
    public void BuildOverrides_ApplyToAll_WritesEveryCharacterTheSame()
    {
        var vm = new FleetShareViewModel("Op", FleetId, [(100, "Main"), (200, "Alt")], Empty())
        {
            ApplyToAll = true,
        };
        Row(vm.AllCharacters, MetricKind.Dps).ChoiceIndex = 1; // share

        var writes = vm.BuildOverrides();

        Assert.Contains(writes, w => w.CharacterId == 100 && w.Key == MetricShareSnapshot.OverrideKeyFor(FleetId, 100, MetricKind.Dps) && w.Value == "true");
        Assert.Contains(writes, w => w.CharacterId == 200 && w.Key == MetricShareSnapshot.OverrideKeyFor(FleetId, 200, MetricKind.Dps) && w.Value == "true");
    }

    [Fact]
    public void BuildOverrides_PerCharacter_WritesIndividually()
    {
        var vm = new FleetShareViewModel("Op", FleetId, [(100, "Main"), (200, "Alt")], Empty())
        {
            ApplyToAll = false,
        };
        Row(vm.Characters.Single(c => c.CharacterId == 100), MetricKind.Location).ChoiceIndex = 1; // main shares location

        var writes = vm.BuildOverrides();

        Assert.Contains(writes, w => w.CharacterId == 100 && w.Key == MetricShareSnapshot.OverrideKeyFor(FleetId, 100, MetricKind.Location) && w.Value == "true");
        // The alt was left on "use global default" → an empty value (no override).
        Assert.Contains(writes, w => w.CharacterId == 200 && w.Key == MetricShareSnapshot.OverrideKeyFor(FleetId, 200, MetricKind.Location) && w.Value == "");
    }

    [AvaloniaFact]
    public void ShareWindow_Renders()
    {
        var vm = new FleetShareViewModel("Saturday roam", FleetId, [(100, "Main"), (200, "Scout")], Empty());
        var window = new FleetShareWindow(vm) { Width = 520, Height = 480 };

        window.Show();
        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-share.png");
    }

    private static MetricShareSnapshot Empty() => new(new Dictionary<string, string>());

    private static FleetMetricShareRowViewModel Row(FleetShareCharacterViewModel character, MetricKind kind) =>
        character.Metrics.Single(m => m.Kind == kind);
}
