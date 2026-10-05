using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The Fleets screen's presence column (ET-440). It read the roster's last-seen alone, a snapshot from the last
/// reload: Moso joined after it and read "unknown" while he was in the fleet and mining. It now reads what the
/// fleet stream says, with the reason, and the card counts who is ready to fly.
/// </summary>
public class FleetsScreenPresenceTests
{
    private const string Server = "eve-together.example:7443";
    private const long FleetId = 26;
    private const int Jithran = 90250177;
    private const int Raymond = 883434905;
    private const int Moso = 2122782452;

    [AvaloniaFact]
    public async Task MemberHeardOnTheStream_ReadsOnline_WithTheReason_AndTheCardCountsWhoIsReady()
    {
        RecordingFleetTransportClient transport = new();
        transport.MyFleetsByServer[Server] =
        [
            new FleetInfo(FleetId, "Sikrah misc", null, FleetVisibility.Public, FleetState.Active, Jithran, null, null,
                DateTimeOffset.UtcNow.AddHours(-1), FleetActivation.Active, ActivatedAt: DateTimeOffset.UtcNow.AddMinutes(-30)),
        ];
        transport.MembersByFleet[FleetId] =
        [
            new FleetMemberInfo(1, Jithran, -1, -1, FleetRole.FleetCommander, false, IsConnected: true),
            new FleetMemberInfo(2, Raymond, 1, 1, FleetRole.SquadMember, false, IsConnected: true,
                LastSeenAt: DateTimeOffset.UtcNow),
            new FleetMemberInfo(3, Moso, 1, 1, FleetRole.SquadMember, false, IsConnected: true),
        ];
        using TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
            services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(true));
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Jithran] = "Jithran", [Raymond] = "RaymondKrah", [Moso] = "Moso Itonula",
            });
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Jithran", Jithran));
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Jithran", Jithran));
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Jithran, FleetId, ClientOnly: false, Jithran, Server, "Sikrah misc")]);

        _ = instance.Services.GetRequiredService<FleetMemberBoard>();
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Moso, FleetId, MetricKind.Presence,
            (double)PresenceState.InGame, nowMs), Moso), EventTarget.Local);
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Moso, FleetId, MetricKind.Shares,
            (double)(SharedMetrics.Combat | SharedMetrics.Mining), nowMs), Moso), EventTarget.Local);
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Raymond, FleetId, MetricKind.Presence,
            (double)PresenceState.NotInGame, nowMs), Raymond), EventTarget.Local);

        FleetsViewModel vm = new(instance.Services, runClock: false);
        FleetViewModel? row = null;
        for (int attempt = 0; attempt < 150 && (row = vm.ServerGroups.SelectMany(group => group.Fleets).FirstOrDefault()) is not { Members.Count: 3 }; attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.NotNull(row);
        vm.Tick(DateTimeOffset.UtcNow);

        FleetMemberRowViewModel moso = row!.Members.Single(member => member.CharacterId == Moso);
        Assert.True(moso.IsOnline);
        Assert.Equal("online", moso.PresenceText);
        Assert.Equal("keeps their location private", moso.PresenceTooltip);
        Assert.Equal("not in game", row.Members.Single(member => member.CharacterId == Raymond).PresenceText);
        Assert.Equal("2/3 ready", row.MemberCountSubText);
        Assert.StartsWith("2 of 3 ready (in game and connected) · 1 offline", row.ReadinessTooltip);

        if (!row.IsExpanded)
            row.ToggleExpandedCommand.Execute(null);
        FleetsWindow window = new(vm) { Width = 1440, Height = 560 };
        window.Show();
        await FleetOfTwo.RunJobsAsync();
        OverlayShots.Capture(window, "et440-fleets-screen-presence");
    }
}
