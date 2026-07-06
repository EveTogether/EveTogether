using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet-metrics graph must show a name for a member who is NOT coupled on this client. Such members are unknown
/// to the connected-set warmup and their samples arrive lazily over the bus, so the row used to render as
/// "Char &lt;id&gt;". The first sample of an unknown id now triggers a best-effort public-ESI lookup that updates the
/// graph label.
/// </summary>
public class FleetMetricsNameResolutionTests
{
    private const int Remote = 90250177;

    [AvaloniaFact]
    public async Task RemoteMemberSample_ResolvesName_NotCharId()
    {
        var lookup = new FakeExternalLookup { [Remote] = "RaymondKrah" };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(lookup));

        // No connected characters → the remote member is unknown to the warmup and must be looked up.
        var fleets = new FakeFleetClient();
        var fleet = new FleetInfo(100, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
        var vm = new FleetMetricsViewModel(instance.Services, fleets, fleet);

        var bus = instance.Services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Remote, fleet.Id, MetricKind.Dps, 123, 0)));

        // The tracker appears with the placeholder, then the lookup resolves and the label updates.
        DpsViewModel? tracker = null;
        for (var i = 0; i < 100; i++)
        {
            tracker = vm.Members.FirstOrDefault();
            if (tracker is not null && tracker.Character == "RaymondKrah")
                break;
            await Task.Delay(50);
        }

        Assert.NotNull(tracker);
        Assert.Equal("RaymondKrah", tracker!.Character);
        Assert.DoesNotContain("Char ", tracker.Character);
    }
}
