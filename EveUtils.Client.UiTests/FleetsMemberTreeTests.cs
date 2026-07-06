using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream B / B-2b: the My Fleets / Participating tabs show each fleet as a node with my characters as member leaves —
/// each with their role, the fit they fly and a SELECT FIT action that picks the pilot's OWN fit
/// (master-plan §5). Drives the real <see cref="FleetsViewModel"/> over a faked transport and renders the My Fleets
/// tab headless (Iron Law #9).
/// </summary>
public class FleetsMemberTreeTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;

    private static FleetInfo Fleet(long id, string name, int owner) =>
        new(id, name, null, FleetVisibility.InviteOnly, FleetState.Active, owner, null, null,
            DateTimeOffset.UnixEpoch, FleetActivation.Forming, null);

    private static FleetMemberInfo Member(int characterId, FleetRole role, FitReferenceInfo? fit) =>
        new(1, characterId, 0, 0, role, false, fit, null);

    private static async Task<FleetsViewModel> LoadedVmAsync(TestClientInstance instance)
    {
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Lionear", Me));
        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);
        return vm;
    }

    [AvaloniaFact]
    public async Task MyFleet_ShowsMyCharacterAsAMemberLeaf_WithRoleAndAssignedFit()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Sat-night HF", Me)];
        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        transport.MembersByFleet[11] = [Member(Me, FleetRole.FleetCommander, fit)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance);

        var leaf = Assert.Single(Assert.Single(Assert.Single(vm.ServerGroups).Fleets).Members);
        Assert.Equal(Me, leaf.CharacterId);
        Assert.Equal("Fleet Commander", leaf.RoleLabel);
        Assert.True(leaf.HasAssignedFit);
        Assert.Equal("Guardian — Armor", leaf.AssignedFit!.FitName);
        Assert.Equal("CHANGE FIT", leaf.SelectFitButtonLabel);
    }

    [AvaloniaFact]
    public async Task SelectFit_OnAMemberLeaf_AssignsThroughTheSinglePicker_AndReloadsWithTheFit()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Sat-night HF", Me)];
        transport.MembersByFleet[11] = [Member(Me, FleetRole.SquadMember, null)];   // unassigned → SELECT FIT

        var recording = new RecordingDialogService();
        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        recording.OnPickFit = _ => Task.FromResult<FitReferenceInfo?>(fit);

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(recording);
        });
        var vm = await LoadedVmAsync(instance);

        var leaf = Assert.Single(Assert.Single(Assert.Single(vm.ServerGroups).Fleets).Members);
        Assert.Equal("SELECT FIT", leaf.SelectFitButtonLabel);

        // The pick persists — make the reload see the new assignment.
        transport.MembersByFleet[11] = [Member(Me, FleetRole.SquadMember, fit)];
        await leaf.SelectFitCommand.ExecuteAsync(null);

        Assert.NotNull(transport.LastAssignedFit);
        Assert.Equal("Guardian — Armor", transport.LastAssignedFit!.Value.Fit!.FitName);
        var refreshed = Assert.Single(Assert.Single(Assert.Single(vm.ServerGroups).Fleets).Members);
        Assert.True(refreshed.HasAssignedFit);
        Assert.Equal("CHANGE FIT", refreshed.SelectFitButtonLabel);
    }

    [AvaloniaFact]
    public async Task MemberLeaf_RefreshesLive_OnFleetChangedEvent()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Sat-night HF", Me)];
        transport.MembersByFleet[11] = [Member(Me, FleetRole.SquadMember, null)];   // no fit yet

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance);

        FleetMemberRowViewModel Leaf() => vm.ServerGroups[0].Fleets[0].Members[0];
        Assert.False(Leaf().HasAssignedFit);

        // A remote member-fit assignment is broadcast as fleet.changed (B-4) — the tabs reload kind-agnostically
        // and pick up the new fit, so a viewer sees it without reopening the window.
        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        transport.MembersByFleet[11] = [Member(Me, FleetRole.SquadMember, fit)];
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(11, FleetChangeKind.RosterChanged)), EventTarget.Local, default);

        for (var i = 0; i < 100 && !Leaf().HasAssignedFit; i++)
            await Task.Delay(50);

        Assert.True(Leaf().HasAssignedFit);
        Assert.Equal("Guardian — Armor", Leaf().AssignedFit!.FitName);
    }

    [AvaloniaFact]
    public async Task FleetsWindow_MyFleetsTab_WithMemberLeaves_Renders()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Sat-night HF", Me)];
        var fit = new FitReferenceInfo(11987, "Guardian — Armor", "{}", "h-guardian", null, null);
        transport.MembersByFleet[11] = [Member(Me, FleetRole.FleetCommander, fit)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var vm = await LoadedVmAsync(instance);

        var window = new FleetsWindow(vm) { Width = 720, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Member leaves render inline under their fleet in the single unified overview (no more tabs to select).
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fleets-member-tree.png");
        window.Close();
    }
}
