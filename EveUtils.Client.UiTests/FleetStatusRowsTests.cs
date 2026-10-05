using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// FLEET in the run window lists every roster member with the reason they read the way they do (ET-440): a pilot who
/// keeps their location private, one not in game, one not connected at all — instead of only those who sent a system,
/// all of them "not sharing a system".
/// </summary>
public class FleetStatusRowsTests
{
    private const int MosoId = 90000004;
    private const int KaelId = 90000005;

    [AvaloniaFact]
    public async Task Fleet_ShowsEveryRosterMember_WithTheirOwnReason()
    {
        RecordingFleetTransportClient transport = new();
        transport.MembersByFleet[FleetOfTwo.FleetId] =
        [
            new FleetMemberInfo(1, FleetOfTwo.JithranId, -1, -1, FleetRole.FleetCommander, false, IsConnected: true),
            new FleetMemberInfo(2, FleetOfTwo.RaymondId, -1, -1, FleetRole.SquadMember, false, IsConnected: true),
            new FleetMemberInfo(3, MosoId, -1, -1, FleetRole.SquadMember, false, IsConnected: false),
            new FleetMemberInfo(4, KaelId, -1, -1, FleetRole.SquadMember, false, IsConnected: true),
        ];
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(kind: ActivityKind.Mining, configure: services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [FleetOfTwo.JithranId] = "Jithran", [FleetOfTwo.RaymondId] = "Raymond",
                [MosoId] = "Moso Itonula", [KaelId] = "Kael",
            });
        });

        await fleet.MineAsync(fleet.Raymond, "Tritanium", 5000);
        await fleet.Raymond.PublishMetricsAsync();
        await fleet.Jithran.Instance.Services.GetRequiredService<IEventBus>().PublishAsync(
            new FleetMetricEvent(new MetricSample(KaelId, FleetOfTwo.FleetId, MetricKind.Presence,
                (double)PresenceState.NotInGame, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), KaelId),
            EventTarget.Local);

        FleetWindowSectionViewModel section = fleet.Jithran.Window.Sections.OfType<FleetWindowSectionViewModel>().Single();
        await fleet.SettleAsync(() =>
        {
            fleet.Jithran.Refresh();
            return fleet.Jithran.Window.FleetMateOreIsk(FleetOfTwo.RaymondId) is not null
                   && section.Rows.Count == 4 && section.Rows.All(row => !row.Name.StartsWith("Char "));
        });
        fleet.Jithran.Refresh();
        await FleetOfTwo.RunJobsAsync();

        Assert.Equal("keeps their location private", _Row(section, "Raymond").SubText);
        Assert.Contains(_Row(section, "Raymond").StatusChips, chip => chip.Text == "in run");
        Assert.Contains(_Row(section, "Raymond").Figures, figure => figure.Label == "ore");
        Assert.Equal("not connected to the server", _Row(section, "Moso Itonula").SubText);
        Assert.Equal("not in game", _Row(section, "Kael").SubText);

        foreach (RunWindowSection shown in fleet.Jithran.Window.Sections)
            shown.IsExpanded = shown is FleetWindowSectionViewModel or MiningWindowSectionViewModel;
        ActivityWindow window = new(fleet.Jithran.Window);
        window.Show();
        window.Width = 600;
        window.Height = 760;
        await FleetOfTwo.RunJobsAsync();
        OverlayShots.Capture(window, "et440-fleet-status-rows");

        foreach (RunWindowSection shown in fleet.Raymond.Window.Sections)
            shown.IsExpanded = shown is FleetWindowSectionViewModel or MiningWindowSectionViewModel;
        fleet.Raymond.Refresh();
        ActivityWindow mate = new(fleet.Raymond.Window);
        mate.Show();
        mate.Width = 600;
        mate.Height = 760;
        await FleetOfTwo.RunJobsAsync();
        OverlayShots.Capture(mate, "et440-fleet-status-rows-member");
    }

    private static FleetCharacterRowViewModel _Row(FleetWindowSectionViewModel section, string name) =>
        section.Rows.Single(row => row.Name == name);
}
