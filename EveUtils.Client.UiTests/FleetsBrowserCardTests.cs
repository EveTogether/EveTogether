using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream B / B-1: the Browser tab shows each discoverable fleet as a card with its coupled doctrine, a per-role fill
/// pill (e.g. "DPS 2 / 40") and a member count, so a pilot sees what a fleet flies and how full each role is before
/// joining. The fill is computed by the shared <see cref="CompositionFillBuilder"/> (also used by the roster).
/// </summary>
public class FleetsBrowserCardTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;

    private static FitReferenceInfo Fit(string name, string hash) => new(11987, name, "{}", hash, null, null);

    private static FleetCompositionDetail Doctrine()
    {
        var muninn = new FleetCompositionEntryInfo(501, 1, null, 0, Fit("Muninn — Kite", "h-muninn"));
        var guardian = new FleetCompositionEntryInfo(601, 2, 3, 0, Fit("Guardian — Armor", "h-guardian"));
        var dps = new FleetCompositionRoleInfo(1, 9, "DPS", 40, 0, [muninn]);
        var logistics = new FleetCompositionRoleInfo(2, 9, "Logistics", 5, 1, [guardian]);
        return new FleetCompositionDetail(
            new FleetCompositionInfo(9, "Homefront Vanguard", null, 999, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [dps, logistics]);
    }

    private static List<FleetMemberInfo> ThreeMembers() =>
    [
        new(1, 1, 0, 0, FleetRole.SquadMember, false, null, 501),   // DPS entry
        new(2, 2, 0, 0, FleetRole.SquadMember, false, null, 501),   // DPS entry
        new(3, 3, 0, 0, FleetRole.SquadMember, false, null, 601),   // Logistics/Guardian entry
    ];

    private static FleetInfo Fleet(long id, string name, int owner, long? compositionId) =>
        new(id, name, null, FleetVisibility.Public, FleetState.Active, owner, null, null,
            DateTimeOffset.UnixEpoch, FleetActivation.Forming, compositionId);

    [Fact]
    public void CompositionFillBuilder_TalliesGroupAndPerFitMinima()
    {
        var fill = CompositionFillBuilder.Build(Doctrine(), ThreeMembers());

        var dps = Assert.Single(fill, r => r.RoleName == "DPS");
        Assert.Equal("2 / 40", dps.GroupCount);

        var logistics = Assert.Single(fill, r => r.RoleName == "Logistics");
        Assert.Equal("1 / 5", logistics.GroupCount);
        var guardian = Assert.Single(logistics.Entries);
        Assert.Equal("Guardian — Armor", guardian.FitName);
        Assert.Equal("1 / 3", guardian.Count);
    }

    [Fact]
    public void CompositionFillBuilder_NoComposition_IsEmpty() =>
        Assert.Empty(CompositionFillBuilder.Build(null, ThreeMembers()));

    [Fact]
    public void AllMinimaMet_ShortFalls_NoComposition_AndMet()
    {
        // The full doctrine wants DPS ≥ 40 / Logistics ≥ 5 / Guardian ≥ 3 — three pilots fall short (B-5).
        Assert.False(CompositionFillBuilder.AllMinimaMet(Doctrine(), ThreeMembers()));

        // No coupled doctrine → nothing to fall short of.
        Assert.True(CompositionFillBuilder.AllMinimaMet(null, ThreeMembers()));

        // A doctrine whose only minimum is DPS ≥ 2, met by the two DPS pilots on entry 501.
        var lowBar = new FleetCompositionDetail(
            new FleetCompositionInfo(9, "Low bar", null, 999, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [new FleetCompositionRoleInfo(1, 9, "DPS", 2, 0, [new FleetCompositionEntryInfo(501, 1, null, 0, Fit("Muninn — Kite", "h-muninn"))])]);
        Assert.True(CompositionFillBuilder.AllMinimaMet(lowBar, ThreeMembers()));
    }

    [AvaloniaFact]
    public async Task BrowserCard_ShowsDoctrineFillAndMemberCount_AndRenders()
    {
        var transport = new RecordingFleetTransportClient();
        transport.OpenFleetsByServer[Server] = [Fleet(22, "Homefront Pug 21:00", 999, 9)];
        transport.CompositionsById[9] = Doctrine();
        transport.MembersByFleet[22] = ThreeMembers();

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Lionear", Me));

        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && vm.ServerGroups.Count == 0; i++)
            await Task.Delay(50);

        var card = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.Equal("Homefront Vanguard", card.CompositionName);
        Assert.Equal(3, card.MemberCount);
        Assert.Equal("2 / 40", Assert.Single(card.RoleFill, r => r.RoleName == "DPS").GroupCount);

        // Browser is the default tab — render it directly (Iron Law #9).
        var window = new FleetsWindow(vm) { Width = 720, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fleets-browser-cards.png");
        window.Close();
    }
}
