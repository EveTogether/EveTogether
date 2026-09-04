using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The collision at the start as the screens present it (ET-168). The rules it guards are the ones an operator
/// decided rather than the code implied: <b>one summary line and one button whatever the count</b>, "leave them" is
/// not "take them off the roster", and the difference between moving <i>your own</i> pilot and merely asking someone
/// else's. Drives the real <see cref="FleetsViewModel"/> over the faked transport.
/// </summary>
public class FleetStartCollisionTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;        // owns Sunday DED run
    private const int Catbank = 200;   // my alt — on both rosters, counting for the earlier fleet
    private const int Aurel = 300;     // someone else, and the earlier fleet's commander
    private const int Tessa = 400;     // someone else's pilot, on both rosters
    private const int Vaari = 500;     // external

    private static readonly DateTimeOffset Earlier = DateTimeOffset.UnixEpoch.AddHours(20);
    private static readonly DateTimeOffset Later = DateTimeOffset.UnixEpoch.AddHours(21);

    private sealed class NoPresence : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => null;
        public bool? IsInGame(int characterId) => null;
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static FleetInfo Fleet(long id, string name, int owner, FleetActivation activation, DateTimeOffset? activatedAt) =>
        new(id, name, null, FleetVisibility.InviteOnly, FleetState.Active, owner, null, null,
            DateTimeOffset.UnixEpoch, activation, ActivatedAt: activatedAt);

    // The fleet commander sits outside the wings (WingId < 0) — that plus the role is what the overview reads.
    private static FleetMemberInfo Member(long memberId, int characterId, FleetRole role = FleetRole.SquadMember, bool external = false) =>
        new(memberId, characterId, role == FleetRole.FleetCommander ? -1 : 0, role == FleetRole.FleetCommander ? -1 : 0,
            role, external, null, null);

    // ── The prompt's arithmetic ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The summary line is counted, not guessed. An external is on the roster but has no client, so counting them as
    /// "available" would overstate what starting achieves; and a pilot who counts elsewhere is not linked by it.
    /// </summary>
    [Fact]
    public void ThePrompt_CountsTheRosterTheWayTheLineReadsIt()
    {
        var prompt = new FleetStartPrompt("Sunday DED run",
        [
            new FleetStartMember(Me, "RaymondKrah", IsMine: true, IsCommander: true, IsExternal: false, null),
            new FleetStartMember(Catbank, "Catbank", IsMine: true, IsCommander: false, IsExternal: false, "Sansha evening"),
            new FleetStartMember(Tessa, "Tessa Korrin", IsMine: false, IsCommander: false, IsExternal: false, "Wormhole night"),
            new FleetStartMember(Aurel, "Aurel Vantis", IsMine: false, IsCommander: false, IsExternal: false, null),
            new FleetStartMember(Vaari, "Vaari Onc", IsMine: false, IsCommander: false, IsExternal: true, null),
        ], CanAskThemAll: true);

        Assert.Equal(5, prompt.RosterCount);
        Assert.Equal(4, prompt.AvailableCount);     // the external has no client
        Assert.Equal(2, prompt.MineCount);
        Assert.Equal(2, prompt.ElsewhereCount);
        Assert.True(prompt.HasCollision);
        Assert.Equal(2, prompt.WillLinkCount);      // me and Aurel; the two elsewhere are not linked by starting
        Assert.Equal("Catbank", Assert.Single(prompt.MyAltsElsewhere).Name);
    }

    /// <summary>Whose pilot it is comes first on the roster line, because it decides which verb applies to them.</summary>
    [Fact]
    public void AMembersStateLine_SaysWhoseTheyAreBeforeWhereTheyAre()
    {
        Assert.Equal("free · will be linked",
            new FleetStartMember(Me, "A", true, false, false, null).StateText);
        Assert.Equal("your pilot · in Sansha evening",
            new FleetStartMember(Catbank, "B", true, false, false, "Sansha evening").StateText);
        Assert.Equal("someone else's pilot · in Wormhole night",
            new FleetStartMember(Tessa, "C", false, false, false, "Wormhole night").StateText);
        Assert.Equal("no client · never shares",
            new FleetStartMember(Vaari, "D", false, false, true, null).StateText);
    }

    // ── Starting ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm, RecordingFleetTransportClient Transport, RecordingDialogService Dialogs)>
        SceneAsync(FleetActivation sunday)
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] =
        [
            Fleet(21, "Sansha evening", Aurel, FleetActivation.Active, Earlier),
            Fleet(22, "Sunday DED run", Me, sunday, sunday == FleetActivation.Active ? Later : null),
        ];
        transport.MembersByFleet[21] = [Member(1, Aurel, FleetRole.FleetCommander), Member(2, Catbank), Member(3, Tessa)];
        transport.MembersByFleet[22] =
        [
            Member(5, Me, FleetRole.FleetCommander), Member(6, Catbank), Member(7, Tessa), Member(8, Vaari, external: true),
        ];
        // Only the server can see that Tessa — not one of my pilots — is flying elsewhere.
        transport.MembersActiveElsewhere.Add(new FleetMemberElsewhereInfo(Tessa, 21, "Sansha evening"));

        var dialogs = new RecordingDialogService();
        var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(dialogs);
            s.AddSingleton<ILocalCharacterPresence>(new NoPresence());
        });

        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("RaymondKrah", Me));
        await registry.AddOrUpdateAsync(new Character("Catbank", Catbank));
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "RaymondKrah", Me));
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Catbank", Catbank));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 100 && vm.ActiveFleets.Count == 0 && vm.StandingByFleets.Count == 0; i++)
            await Task.Delay(50);
        return (instance, vm, transport, dialogs);
    }

    /// <summary>
    /// The dialog is told the whole collision, from both halves of it: my own alt worked out here (this client can
    /// see both fleets), someone else's pilot taken from the server (no client could).
    /// </summary>
    [AvaloniaFact]
    public async Task Starting_ShowsBothHalvesOfTheCollision()
    {
        var (instance, vm, _, dialogs) = await SceneAsync(FleetActivation.Forming);
        using (instance)
        {
            dialogs.FleetStart = FleetStartChoice.Cancel;
            var row = Assert.Single(vm.StandingByFleets, r => r.Name == "Sunday DED run");
            await vm.StartRowCommand.ExecuteAsync(row);

            Assert.NotNull(dialogs.FleetStartPrompt);
            var prompt = dialogs.FleetStartPrompt!;
            Assert.Equal("Sunday DED run", prompt.FleetName);
            Assert.Equal(2, prompt.ElsewhereCount);
            Assert.Equal("Sansha evening", Assert.Single(prompt.Members, m => m.CharacterId == Catbank).ElsewhereFleetName);
            Assert.Equal("Sansha evening", Assert.Single(prompt.Members, m => m.CharacterId == Tessa).ElsewhereFleetName);
            Assert.Equal("Catbank", Assert.Single(prompt.MyAltsElsewhere).Name);
            vm.Dispose();
        }
    }

    /// <summary>Cancelling starts nothing, which is the only way out of the dialog that does not.</summary>
    [AvaloniaFact]
    public async Task Cancelling_StartsNothingAndAsksNobody()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync(FleetActivation.Forming);
        using (instance)
        {
            dialogs.FleetStart = FleetStartChoice.Cancel;
            await vm.StartRowCommand.ExecuteAsync(Assert.Single(vm.StandingByFleets, r => r.Name == "Sunday DED run"));

            Assert.Empty(transport.SwitchRequestCalls);
            vm.Dispose();
        }
    }

    /// <summary>
    /// "Leave them" is the default and the whole of it: the fleet runs, and nobody is asked and nobody is moved. It
    /// is deliberately not the same act as taking them off the roster — an earlier design made it that, and it shut
    /// the door on the member who switches an hour later.
    /// </summary>
    [AvaloniaFact]
    public async Task LeavingThemWhereTheyAre_StartsTheFleetAndAsksNobody()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync(FleetActivation.Forming);
        using (instance)
        {
            dialogs.FleetStart = FleetStartChoice.LeaveThem;
            var row = Assert.Single(vm.StandingByFleets, r => r.Name == "Sunday DED run");
            await vm.StartRowCommand.ExecuteAsync(row);

            Assert.Empty(transport.SwitchRequestCalls);
            Assert.Empty(transport.LeaveCalls);
            vm.Dispose();
        }
    }

    /// <summary>
    /// One button for all of them: a single call, with no member named, whether one member is elsewhere or fifty.
    /// And it goes out <i>after</i> the start — asking someone to leave a running fleet for one that is not running
    /// is asking them to count for nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task AskingThemAll_SendsOneRequestForTheWholeFleet()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync(FleetActivation.Forming);
        using (instance)
        {
            dialogs.FleetStart = FleetStartChoice.AskThemAll;
            await vm.StartRowCommand.ExecuteAsync(Assert.Single(vm.StandingByFleets, r => r.Name == "Sunday DED run"));

            var call = Assert.Single(transport.SwitchRequestCalls);
            Assert.Equal(22L, call.FleetId);
            Assert.Equal(0, call.OnlyCharacterId);   // 0 = every member who is elsewhere
            Assert.Empty(transport.LeaveCalls);      // it asks; it moves nobody
            vm.Dispose();
        }
    }

    // ── The member row ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule the mockup states in one line and the code has to keep: <b>your own pilot you move, anyone else's
    /// you ask</b>. Two verbs on the same row, and which one shows turns on whose character it is — never on being
    /// the fleet commander.
    /// </summary>
    [AvaloniaFact]
    public async Task AMemberWhoCountsElsewhere_IsMovedIfMineAndAskedIfNot()
    {
        var (instance, vm, _, _) = await SceneAsync(FleetActivation.Active);
        using (instance)
        {
            var row = Assert.Single(vm.ActiveFleets, r => r.Name == "Sunday DED run");

            var mine = Assert.Single(row.Members, m => m.CharacterId == Catbank);
            Assert.True(mine.IsElsewhereActive);
            Assert.True(mine.CanSwitch);
            Assert.Equal("switch", mine.SwitchLabel);

            var theirs = Assert.Single(row.Members, m => m.CharacterId == Tessa);
            Assert.True(theirs.IsElsewhereActive);
            Assert.True(theirs.CanSwitch);
            Assert.Equal("ask to switch", theirs.SwitchLabel);

            // Nobody who counts here has anything to switch to — the act belongs to the collision and nowhere else.
            var linked = Assert.Single(row.Members, m => m.CharacterId == Me);
            Assert.False(linked.CanSwitch);
            Assert.Null(linked.SwitchCommand);
            vm.Dispose();
        }
    }

    /// <summary>Asking someone else's pilot is the same request narrowed to them, and it moves nobody.</summary>
    [AvaloniaFact]
    public async Task AskingOneMemberToSwitch_NamesThatMemberAndMovesNobody()
    {
        var (instance, vm, transport, _) = await SceneAsync(FleetActivation.Active);
        using (instance)
        {
            var row = Assert.Single(vm.ActiveFleets, r => r.Name == "Sunday DED run");
            await Assert.Single(row.Members, m => m.CharacterId == Tessa).SwitchCommand!.ExecuteAsync(null);

            var call = Assert.Single(transport.SwitchRequestCalls);
            Assert.Equal(22L, call.FleetId);
            Assert.Equal(Tessa, call.OnlyCharacterId);
            Assert.Empty(transport.LeaveCalls);
            vm.Dispose();
        }
    }

    /// <summary>
    /// Moving my own alt: it comes off the roster of the fleet it counted for, and off no other. The fleet it is
    /// being moved <i>to</i> is one it is already on — so there is nothing to join, and nothing that could half-fail.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingMyOwnAlt_LeavesTheFleetItCountedFor_AndOnlyThatOne()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync(FleetActivation.Active);
        using (instance)
        {
            dialogs.FleetSwitch = true;
            var row = Assert.Single(vm.ActiveFleets, r => r.Name == "Sunday DED run");
            await Assert.Single(row.Members, m => m.CharacterId == Catbank).SwitchCommand!.ExecuteAsync(null);

            Assert.NotNull(dialogs.FleetSwitchPrompt);
            var prompt = dialogs.FleetSwitchPrompt!;
            Assert.Equal("Catbank", prompt.CharacterName);
            Assert.Equal("Sunday DED run", prompt.TargetFleetName);
            Assert.Equal("Sansha evening", Assert.Single(prompt.Leaving).FleetName);

            var call = Assert.Single(transport.LeaveCalls);
            Assert.Equal(21L, call.FleetId);          // the earlier fleet, not the one being switched to
            Assert.Equal(Catbank, call.ActingCharacterId);
            Assert.Empty(transport.SwitchRequestCalls);   // moving my own pilot is not asking anybody
            vm.Dispose();
        }
    }

    /// <summary>Backing out of the dialog leaves both rosters exactly as they were.</summary>
    [AvaloniaFact]
    public async Task BackingOutOfTheSwitch_LeavesNothing()
    {
        var (instance, vm, transport, dialogs) = await SceneAsync(FleetActivation.Active);
        using (instance)
        {
            dialogs.FleetSwitch = false;
            var row = Assert.Single(vm.ActiveFleets, r => r.Name == "Sunday DED run");
            await Assert.Single(row.Members, m => m.CharacterId == Catbank).SwitchCommand!.ExecuteAsync(null);

            Assert.Empty(transport.LeaveCalls);
            vm.Dispose();
        }
    }

    /// <summary>
    /// The band says the same thing the row does: an elsewhere-active pilot's lane offers the switch, and the
    /// commander's lane — which cannot walk out of its own fleet — does not.
    /// </summary>
    [AvaloniaFact]
    public async Task TheLaneOfAPilotWhoCountsElsewhere_OffersTheSwitchAndTheLeave()
    {
        var (instance, vm, _, _) = await SceneAsync(FleetActivation.Active);
        using (instance)
        {
            var lane = Assert.Single(vm.Lanes, l => l.CharacterId == Catbank);
            Assert.True(lane.IsElsewhereActive);
            Assert.Equal("switch", lane.PrimaryActionText);
            Assert.True(lane.PrimaryIsWarn);
            Assert.Equal("leave", lane.SecondaryActionText);

            var commander = Assert.Single(vm.Lanes, l => l.CharacterId == Me);
            Assert.Equal("STOP", commander.PrimaryActionText);
            Assert.False(commander.PrimaryIsWarn);
            vm.Dispose();
        }
    }
}
