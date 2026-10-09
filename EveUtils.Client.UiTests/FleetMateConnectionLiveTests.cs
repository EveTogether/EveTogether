using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
/// ET-492: a fleet mate who connected while the Fleets screen, Manage or the start dialog was open kept reading
/// "no link" until the screen was reopened. The connection flag came down once with the roster and nothing told the
/// screen it changed; the server's <see cref="FleetMateConnectionEvent"/> now makes each of them read it again.
/// </summary>
public class FleetMateConnectionLiveTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;
    private const int Tessa = 400;   // someone else's pilot, on another machine
    private const long FleetId = 22;

    private static readonly FleetInfo Sunday = new(FleetId, "Sunday DED run", null, FleetVisibility.InviteOnly,
        FleetState.Active, Me, null, null, DateTimeOffset.UnixEpoch, FleetActivation.Forming);

    private static IReadOnlyList<FleetMemberInfo> Roster(bool tessaConnected) =>
    [
        new(5, Me, -1, -1, FleetRole.FleetCommander, false, IsConnected: true),
        new(6, Tessa, 0, 0, FleetRole.SquadMember, false, IsConnected: tessaConnected),
    ];

    [AvaloniaFact]
    public async Task Overview_AMateConnects_ReadsConnectedWithoutReopening()
    {
        var (instance, vm, transport, _) = await SceneAsync();
        using (instance)
        {
            Assert.Equal(FleetMemberStatusReason.NotConnected, Tessa_(vm).StatusReason);

            await ConnectTessaAsync(instance, () => transport.MembersByFleet[FleetId] = Roster(tessaConnected: true),
                () => Tessa_(vm).StatusReason == FleetMemberStatusReason.Connected);

            Assert.Equal(FleetMemberStatusReason.Connected, Tessa_(vm).StatusReason);
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Overview_Disposed_NoLongerReloadsOnAConnection()
    {
        var (instance, vm, transport, _) = await SceneAsync();
        using (instance)
        {
            vm.Dispose();

            await ConnectTessaAsync(instance, () => transport.MembersByFleet[FleetId] = Roster(tessaConnected: true),
                () => false);

            Assert.Equal(FleetMemberStatusReason.NotConnected, Tessa_(vm).StatusReason);
        }
    }

    [AvaloniaFact]
    public async Task StartDialog_AMateConnectsWhileItIsOpen_TheRosterFollows()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync();
        using (instance)
        {
            FleetStartMember? before = null, after = null;
            dialogs.WhileFleetStartOpen = async roster =>
            {
                before = Assert.Single(roster.Current.Members, m => m.CharacterId == Tessa);
                await ConnectTessaAsync(instance, () => transport.MembersByFleet[FleetId] = Roster(tessaConnected: true),
                    () => roster.Current.Members.Single(m => m.CharacterId == Tessa).IsConnectedOnly);
                after = Assert.Single(roster.Current.Members, m => m.CharacterId == Tessa);
            };

            await vm.StartRowCommand.ExecuteAsync(Assert.Single(vm.StandingByFleets));

            Assert.Equal(FleetMemberStatusReason.NotConnected, before?.StatusReason);
            Assert.Equal(FleetMemberStatusReason.Connected, after?.StatusReason);
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void StartWindow_RedrawsOnARosterChange_AndLetsGoOfItWhenClosed()
    {
        var members = new List<FleetStartMember>
        {
            new(Me, "RaymondKrah", true, true, false, null, Presence: FleetMemberPresenceState.Online, IsConnected: true),
            new(Tessa, "Tessa", false, false, false, null, StatusReason: FleetMemberStatusReason.NotConnected,
                StatusText: "no link", IsConnected: false),
        };
        var roster = new FleetStartRoster(new FleetStartPrompt("Sunday DED run", [.. members], CanAskThemAll: true),
            () => [.. members]);
        var window = new StartFleetWindow(roster) { Width = 560, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        members[1] = members[1] with { StatusReason = FleetMemberStatusReason.Connected, StatusText = "connected", IsConnected = true };
        roster.Refresh();

        Assert.Equal("connected", window.Members.Single(m => m.CharacterId == Tessa).StatusWord);
        Assert.True(roster.HasListeners);

        window.Close();

        Assert.False(roster.HasListeners);
    }

    [AvaloniaFact]
    public async Task Manage_AMateConnects_ReadsConnectedWithoutReopening()
    {
        var instance = TestClientInstance.Create(s => s.AddSingleton<ILocalCharacterPresence>(new NoPresence()));
        using (instance)
        {
            var fleets = new FakeFleetClient
            {
                Members = Roster(tessaConnected: false),
                Wings = [new FleetWingInfo(1, FleetId, "Wing 1")],
                Squads = [new FleetSquadInfo(1, 1, "Squad 1")],
            };
            using var roster = new FleetRosterViewModel(instance.Services, fleets, Sunday, isOwner: true, Me);
            for (var i = 0; i < 200 && TessaNode()?.StatusReason is null; i++)
                await Task.Delay(20);
            Assert.Equal(FleetMemberStatusReason.NotConnected, TessaNode()?.StatusReason);

            await ConnectTessaAsync(instance, () => fleets.Members = Roster(tessaConnected: true),
                () => TessaNode()?.StatusReason == FleetMemberStatusReason.Connected);

            Assert.Equal(FleetMemberStatusReason.Connected, TessaNode()?.StatusReason);

            MemberNodeViewModel? TessaNode() => roster.Entries.SingleOrDefault(e => e.Member?.CharacterId == Tessa)?.Node;
        }
    }

    private static FleetMemberRowViewModel Tessa_(FleetsViewModel vm) =>
        Assert.Single(Assert.Single(vm.StandingByFleets).Members, m => m.CharacterId == Tessa);

    /// <summary>The server's word that Tessa connected, after the roster it would be re-read from already says so.</summary>
    private static async Task ConnectTessaAsync(TestClientInstance instance, Action serverNowSays, Func<bool> settled)
    {
        serverNowSays();
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(
            new FleetMateConnectionEvent(new FleetMateConnectionPayload(Tessa, IsConnected: true)), EventTarget.Local);
        for (var i = 0; i < 100 && !settled(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm, RecordingFleetTransportClient Transport, RecordingDialogService Dialogs)>
        SceneAsync()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Sunday];
        transport.MembersByFleet[FleetId] = Roster(tessaConnected: false);

        var dialogs = new RecordingDialogService { FleetStart = FleetStartChoice.Cancel };
        var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(dialogs);
            s.AddSingleton<ILocalCharacterPresence>(new NoPresence());
        });

        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("RaymondKrah", Me));
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "RaymondKrah", Me));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 100 && vm.StandingByFleets.Count == 0; i++)
            await Task.Delay(50);
        Assert.Single(vm.StandingByFleets);
        return (instance, vm, transport, dialogs);
    }

    private sealed class NoPresence : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => null;
        public bool? IsInGame(int characterId) => null;
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
