using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-170, screen 12: an unfolded fleet row answers three questions — are my pilots in it, do they count, and is
/// anything wrong — and not "what are all fifty called". So a roster of six or fewer is drawn whole
/// (<see cref="FleetViewModel.UnfoldedRosterLimit"/>, screen 1 shows its externals by name), and a longer one folds
/// to the members that matter: the fleet commander, this client's own characters, and whoever asks for attention.
/// Everyone else is a tally on one line with "show all N". A fleet of fifty costs the height of a fleet of six, and
/// a member who is not linked or shares nothing can never disappear behind that click.
///
/// Supersedes ET-53's "first six of the roster" fold, which listed pilots by position rather than by relevance.
/// </summary>
public class FleetRowMemberFoldTests
{
    private const int Owner = 95200001;
    private const int Alt = 95200002;
    private const int FirstExternal = 96200001;

    private static TestClientInstance CreateInstance(RecordingDialogService? dialogs = null) =>
        TestClientInstance.Create(services =>
        {
            var lookup = new FakeExternalLookup { [Owner] = "Jithran", [Alt] = "Abnoba Auscent" };
            for (var i = 0; i < 60; i++)
                lookup[FirstExternal + i] = $"Pilot {i:00}";
            services.AddSingleton<IExternalCharacterLookup>(lookup);
            if (dialogs is not null)
                services.AddSingleton<IDialogService>(dialogs);
        });

    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 300)
    {
        for (var i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return true;
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>A client-only fleet of <paramref name="size"/> pilots: the owner (its FC), optionally this client's
    /// alt, and external pilots for the rest — which is how a local fleet of any size is actually built, since an
    /// external is the only way to add a pilot who is not signed in here.</summary>
    private static async Task<long> SeedFleetAsync(TestClientInstance instance, int size, bool withAlt = true)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));
        if (withAlt)
            await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        var created = await fleets.CreateLocalFleetAsync("Home Fleet", null, Owner);
        Assert.True(created.IsSuccess);

        var placed = 1;   // the creator is already a member, as this fleet's fleet commander
        if (withAlt)
        {
            Assert.True((await fleets.AddLocalCharacterAsync(created.Value, Alt, Owner)).IsSuccess);
            placed++;
        }

        for (var i = 0; placed < size; i++, placed++)
            Assert.True((await fleets.AddExternalAsync(created.Value, FirstExternal + i, Owner)).IsSuccess);

        return created.Value;
    }

    private static async Task<FleetViewModel> RowAsync(TestClientInstance instance, FleetsViewModel vm, int size)
    {
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count == size),
            $"the local fleet's row never loaded its {size} members");
        return vm.LocalFleets[0];
    }

    private static async Task<(FleetsViewModel Vm, FleetViewModel Row)> LoadAsync(TestClientInstance instance, int size)
    {
        var vm = new FleetsViewModel(instance.Services, runClock: false);
        return (vm, await RowAsync(instance, vm, size));
    }

    private static IReadOnlyList<string> Painted(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return root.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static FleetMemberRowViewModel Leaf(int characterId, string name, bool isMine = false, bool isFc = false, bool external = false) =>
        new(characterId, characterId, name, isFc ? "Fleet Commander" : "Squad Member", null, null,
            new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask),
            menuFacts: new FleetMemberFacts(name, isFc ? FleetRole.FleetCommander : FleetRole.SquadMember, external),
            isMine: isMine, isFleetCommander: isFc);

    // --- The small fleet: drawn whole ------------------------------------------------------------------------------

    /// <summary>Six or fewer is what screen 1 draws in full, externals by name. No fold line, no extra click, and every
    /// member on screen — the fold may not cost the common case anything.</summary>
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ASmallFleet_ShowsEveryMember_AndNoFoldLine(int size)
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, size);
        var (vm, row) = await LoadAsync(instance, size);

        Assert.False(row.CanShortenMembers);
        Assert.Equal(size, row.VisibleMembers.Count);
        Assert.Equal(0, row.HiddenMemberCount);
        Assert.Equal($"{size} in fleet", row.MemberCountLabel);

        row.ToggleExpandedCommand.Execute(null);
        var window = new FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        var painted = Painted(window);
        Assert.DoesNotContain(painted, t => t.StartsWith("show all", StringComparison.Ordinal));
        Assert.Contains("Jithran", painted);
        if (size > 2)
            Assert.Contains("Pilot 00", painted);   // an external, named on the row like anyone else

        window.Close();
        vm.Dispose();
    }

    // --- The fifty-man fleet: the case the ticket is about ---------------------------------------------------------

    /// <summary>Fifty pilots under one row. My two characters, a tally for the other forty-eight, and the fleet's real
    /// size on the row regardless.</summary>
    [AvaloniaFact]
    public async Task AFiftyManFleet_ShowsTheMembersThatMatter_ATallyForTheRest_AndTheTotal()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);

        Assert.True(row.CanShortenMembers);
        Assert.Equal(2, row.VisibleMembers.Count);
        Assert.Equal(48, row.HiddenMemberCount);
        Assert.Equal("show all 50", row.MoreMembersLabel);
        Assert.Equal("and 48 others — the fleet counts:", row.HiddenMembersText);
        Assert.Contains(row.HiddenMemberChips, c => c.Text == "48 external");
        Assert.Equal("50 in fleet", row.MemberCountLabel);

        vm.Dispose();
    }

    /// <summary>
    /// And on screen, which is the whole point: at fifty members the unfolded row really paints two leaves and the
    /// tally line, not fifty rows. Read off the rendered visual tree, because a shortened list that only exists in the
    /// view-model is the failure mode this screen family keeps producing (ET-30, ET-43, ET-49).
    /// </summary>
    [AvaloniaFact]
    public async Task AFiftyManFleet_RendersTwoLeavesAndTheTallyLine_NotFiftyRows()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);
        row.ToggleExpandedCommand.Execute(null);

        var window = new FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        ItemsControl leaves = window.GetVisualDescendants().OfType<ItemsControl>()
            .First(c => ReferenceEquals(c.ItemsSource, row.VisibleMembers));
        Assert.Equal(2, leaves.ItemCount);

        var painted = Painted(window);
        Assert.Contains("show all 50", painted);
        Assert.Contains("and 48 others — the fleet counts:", painted);
        Assert.Contains("48 external", painted);
        Assert.Contains("Jithran", painted);                                  // the FC, first of the two
        Assert.DoesNotContain("Pilot 47", painted);                           // deep in the tail, and folded away

        Assert.NotNull(window.CaptureRenderedFrame());   // it really painted; the above is what it painted
        window.Close();
        vm.Dispose();
    }

    // --- Which members survive the fold ----------------------------------------------------------------------------

    /// <summary>Who a folded row shows: the fleet commander first, then this client's own characters. Listing the
    /// first few off the roster would be the one ordering that serves nobody.</summary>
    [AvaloniaFact]
    public async Task TheFoldedList_ShowsTheFleetCommanderFirst_ThenMyOwnCharacters()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);

        Assert.Equal(Owner, row.VisibleMembers[0].CharacterId);   // the FC
        Assert.Equal(Alt, row.VisibleMembers[1].CharacterId);     // my own character
        Assert.Equal(2, row.VisibleMembers.Count);

        vm.Dispose();
    }

    /// <summary>The member the fold may never hide: one who is on the roster of a started fleet but counts for another,
    /// or one of mine who shares nothing. They stay on the short list however long the roster is, with an amber
    /// tally beside the "show all".</summary>
    [AvaloniaFact]
    public void AMemberWhoAsksForAttention_IsNeverBehindTheFold()
    {
        var fleet = new FleetInfo(7, "Server Op", null, FleetVisibility.Public, FleetState.Active, Owner,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
        var row = new FleetViewModel(fleet, Owner, "Jithran", "server:1", "Test Server") { MemberCount = 12 };

        row.Members.Add(Leaf(Owner, "Jithran", isMine: true, isFc: true));
        for (var i = 0; i < 10; i++)
            row.Members.Add(Leaf(FirstExternal + i, $"Pilot {i:00}"));
        var elsewhere = Leaf(FirstExternal + 40, "Bex Harrow");
        row.Members.Add(elsewhere);
        foreach (var member in row.Members)
            member.LinkState = FleetMemberLinkState.Linked;
        elsewhere.LinkState = FleetMemberLinkState.ElsewhereActive;
        row.Members[3].SharesNothing = true;
        row.RefreshVisibleMembers();

        Assert.True(row.CanShortenMembers);
        Assert.Equal(new[] { "Jithran", "Pilot 02", "Bex Harrow" }, row.VisibleMembers.Select(m => m.CharacterName));   // FC, then attention in roster order
        Assert.Equal("show all 12", row.MoreMembersLabel);
        Assert.Contains(row.HiddenMemberChips, c => c.Text == "1 elsewhere active" && c.IsWarning);
        Assert.Contains(row.HiddenMemberChips, c => c.Text == "1 shares nothing" && c.IsWarning);
        Assert.Contains(row.HiddenMemberChips, c => c.Text == "11 linked");
    }

    // --- The fold line's behaviour --------------------------------------------------------------------------------

    /// <summary>The fold line opens the rest inline and closes it again. Inline rather than a jump to the roster
    /// window: this screen is the overview, and the question it is being asked ("who else is in this fleet") is one an
    /// overview should answer where it stands. MANAGE remains the route to the structure itself.</summary>
    [AvaloniaFact]
    public async Task TheFoldLine_OpensTheRestInline_AndClosesItAgain()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);
        row.ToggleExpandedCommand.Execute(null);

        var window = new FleetsWindow(vm) { Width = 760, Height = 900 };
        window.Show();

        row.ToggleMembersCommand.Execute(null);

        Assert.True(row.MembersExpanded);
        Assert.Equal(50, row.VisibleMembers.Count);
        Assert.Equal("show fewer", row.MoreMembersLabel);
        Assert.Contains("Pilot 47", Painted(window));   // a pilot who was under the line is now on screen

        row.ToggleMembersCommand.Execute(null);

        Assert.False(row.MembersExpanded);
        Assert.Equal(2, row.VisibleMembers.Count);
        Assert.Equal("show all 50", row.MoreMembersLabel);

        window.Close();
        vm.Dispose();
    }

    /// <summary>Unfolding survives a reload. A removal reloads the whole list (ET-52), so without this the FC's
    /// unfolded fifty-man row snapped shut the instant they removed someone from it.</summary>
    [AvaloniaFact]
    public async Task AnUnfoldedRow_StaysUnfolded_AcrossAReload()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);

        row.ToggleExpandedCommand.Execute(null);
        row.ToggleMembersCommand.Execute(null);
        Assert.True(row.MembersExpanded);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count == 50));

        Assert.True(vm.LocalFleets[0].IsExpanded);
        Assert.True(vm.LocalFleets[0].MembersExpanded);
        Assert.Equal(50, vm.LocalFleets[0].VisibleMembers.Count);

        vm.Dispose();
    }

    // --- Where ET-52 and the tally meet ---------------------------------------------------------------------------

    /// <summary>The number on the tally line is a fact about the fleet, so it has to be right the moment the fleet
    /// changes — including when the change was made on another screen entirely (ET-52).</summary>
    [AvaloniaFact]
    public async Task RemovingAPilotElsewhere_CorrectsTheTallyAndTheTotal_AtOnce()
    {
        using var instance = CreateInstance(new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) });
        long fleetId = await SeedFleetAsync(instance, 50);
        var (vm, row) = await LoadAsync(instance, 50);
        Assert.Equal("show all 50", row.MoreMembersLabel);

        // Removed in fleet metrics, one screen over — the operator's ET-52 case, on a fifty-man row.
        var client = new LocalFleetClient(
            instance.Services.GetRequiredService<ClientFleetService>(),
            instance.Services.GetRequiredService<IFleetRepository>(),
            instance.Services.GetRequiredService<ICharacterRegistry>(),
            Owner);
        var victim = (await client.ListMembersAsync(fleetId)).Last();
        var (status, _) = await instance.Services.GetRequiredService<FleetMemberRemovalService>().RemoveAsync(
            client, new FleetMemberRemovalRequest(fleetId, victim.Id, victim.CharacterId, "Pilot 47", "Home Fleet"));
        Assert.Equal(FleetMemberRemovalStatus.RemovedFromFleet, status);

        Assert.True(await WaitForAsync(() => vm.LocalFleets[0].Members.Count == 49),
            "the row did not pick up the removal");
        Assert.Equal("show all 49", vm.LocalFleets[0].MoreMembersLabel);
        Assert.Equal("and 47 others — the fleet counts:", vm.LocalFleets[0].HiddenMembersText);
        Assert.Equal("49 in fleet", vm.LocalFleets[0].MemberCountLabel);

        vm.Dispose();
    }
}
