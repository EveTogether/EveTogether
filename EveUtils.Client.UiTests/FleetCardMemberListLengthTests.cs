using System;
using System.Collections.Generic;
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
/// ET-53: the fleet overview's cards stop being an overview when one of them lists fifty pilots. Since ET-46 a
/// client-only fleet's card carries its WHOLE roster — the right fix there, because an external pilot has a row
/// nowhere else — and PR #66 deliberately left "40 leaves on a browser card" for a separate decision. This is it.
///
/// What was decided, and what these tests pin down:
/// <list type="bullet">
/// <item><b>Six visible</b> (<see cref="FleetViewModel.CollapsedMemberLimit"/>). Five was the suggestion; six leaves
/// a slot beyond the FC and a four-alt multiboxer's own characters, which is where the interesting pilots start.</item>
/// <item><b>Which six</b>: fleet commander, then this client's own characters, then external pilots, then the rest in
/// roster order. First-five-off-the-roster is the one ordering that answers nobody's question.</item>
/// <item><b>The fold line expands inline</b> rather than sending the user to the roster window: the card is the only
/// place an external pilot appears at all, so "who else is in here" has to be answerable where it is asked.</item>
/// <item><b>The total is always on the card</b>, folded or not — how big a fleet is is information in itself.</item>
/// <item><b>A small fleet is untouched</b>: no fold line, no extra click.</item>
/// <item><b>Server cards fold too</b>, on the same rule. What each card LISTS is unchanged (a server fleet's card is
/// still my own characters); only how many of that list it draws before folding is new.</item>
/// </list>
/// </summary>
public class FleetCardMemberListLengthTests
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

    private static async Task<FleetViewModel> CardAsync(TestClientInstance instance, FleetsViewModel vm, int size)
    {
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count == size),
            $"the local fleet's card never loaded its {size} members");
        return vm.LocalFleets[0];
    }

    private static async Task<(FleetsViewModel Vm, FleetViewModel Card)> LoadAsync(TestClientInstance instance, int size)
    {
        var vm = new FleetsViewModel(instance.Services);
        return (vm, await CardAsync(instance, vm, size));
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

    // --- The small fleet: nothing changes ------------------------------------------------------------------------

    /// <summary>A fleet of a handful is what this card was already good at. No fold line, no extra click, and every
    /// member on screen — the shortening may not cost the common case anything.</summary>
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ASmallFleet_ShowsEveryMember_AndNoFoldLine(int size)
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, size);
        var (vm, card) = await LoadAsync(instance, size);

        Assert.False(card.CanShortenMembers);
        Assert.Equal(size, card.VisibleMembers.Count);
        Assert.Equal($"{size} in fleet", card.MemberCountLabel);

        var window = new FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        var painted = Painted(window);
        Assert.DoesNotContain(painted, t => t.Contains("more", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Jithran", painted);

        window.Close();
        vm.Dispose();
    }

    // --- The fifty-man fleet: the case the ticket is about ---------------------------------------------------------

    /// <summary>Fifty pilots under one card, which is what the operator wanted headed off. Six leaves, a fold line for
    /// the other forty-four, and the fleet's real size on the card regardless.</summary>
    [AvaloniaFact]
    public async Task AFiftyManFleet_ShowsSixMembers_AFoldLineForTheRest_AndTheTotal()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, card) = await LoadAsync(instance, 50);

        Assert.True(card.CanShortenMembers);
        Assert.Equal(FleetViewModel.CollapsedMemberLimit, card.VisibleMembers.Count);
        Assert.Equal(6, card.VisibleMembers.Count);
        Assert.Contains("+ 44 more", card.MoreMembersLabel);
        Assert.Equal("50 in fleet", card.MemberCountLabel);

        vm.Dispose();
    }

    /// <summary>
    /// And on screen, which is the whole point of the ticket: at fifty members the card really paints six leaves and
    /// the fold line, not fifty rows. Read off the rendered visual tree, because a shortened list that only exists in
    /// the view-model is the failure mode this screen family keeps producing (ET-30, ET-43, ET-49).
    /// </summary>
    [AvaloniaFact]
    public async Task AFiftyManFleet_RendersSixLeavesAndTheFoldLine_NotFiftyRows()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        await SeedFleetAsync(instance, 50);
        var (vm, card) = await LoadAsync(instance, 50);

        var window = new FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        ItemsControl leaves = window.GetVisualDescendants().OfType<ItemsControl>()
            .First(c => ReferenceEquals(c.ItemsSource, card.VisibleMembers));
        Assert.Equal(6, leaves.ItemCount);

        var painted = Painted(window);
        Assert.Contains(painted, t => t.Contains("+ 44 more", StringComparison.Ordinal));
        Assert.Contains("50 in fleet", painted);
        Assert.Contains("Jithran", painted);                                  // the FC, first of the six
        Assert.DoesNotContain("Pilot 47", painted);                           // deep in the tail, and folded away

        Assert.NotNull(window.CaptureRenderedFrame());   // it really painted; the above is what it painted
        window.Close();
        vm.Dispose();
    }

    // --- Which members survive the fold ----------------------------------------------------------------------------

    /// <summary>Who a folded card shows: the fleet commander first, then this client's own characters, then external
    /// pilots, then everyone else. Listing the first six off the roster would be the one ordering that serves nobody.</summary>
    [AvaloniaFact]
    public async Task TheFoldedList_ShowsTheFleetCommanderFirst_ThenMyOwnCharacters()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, card) = await LoadAsync(instance, 50);

        Assert.Equal(Owner, card.VisibleMembers[0].CharacterId);   // the FC
        Assert.Equal(Alt, card.VisibleMembers[1].CharacterId);     // my own character
        Assert.All(card.VisibleMembers.Skip(2), m => Assert.True(m.IsExternal));

        vm.Dispose();
    }

    /// <summary>An external pilot has a row on this card and nowhere else in the client (ET-46), so a fold that hides
    /// some of them says so rather than counting them silently — the FC can see there is something under the line.</summary>
    [AvaloniaFact]
    public async Task HiddenExternalPilots_AreNamedOnTheFoldLine()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 20);
        var (vm, card) = await LoadAsync(instance, 20);

        // 20 members: the FC, my alt, 18 externals. Six shown → 14 hidden, all of them external.
        Assert.Equal("▾ + 14 more · 14 external", card.MoreMembersLabel);

        vm.Dispose();
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
        var (vm, card) = await LoadAsync(instance, 50);

        var window = new FleetsWindow(vm) { Width = 760, Height = 900 };
        window.Show();

        card.ToggleMembersCommand.Execute(null);

        Assert.True(card.MembersExpanded);
        Assert.Equal(50, card.VisibleMembers.Count);
        Assert.Contains("SHOW FEWER", card.MoreMembersLabel);
        Assert.Contains("Pilot 47", Painted(window));   // a pilot who was under the line is now on screen

        card.ToggleMembersCommand.Execute(null);

        Assert.False(card.MembersExpanded);
        Assert.Equal(6, card.VisibleMembers.Count);
        Assert.Contains("+ 44 more", card.MoreMembersLabel);

        window.Close();
        vm.Dispose();
    }

    /// <summary>Unfolding survives a reload. A removal reloads the whole list (ET-52), so without this the FC's
    /// unfolded fifty-man card snapped shut the instant they removed someone from it.</summary>
    [AvaloniaFact]
    public async Task AnUnfoldedCard_StaysUnfolded_AcrossAReload()
    {
        using var instance = CreateInstance();
        await SeedFleetAsync(instance, 50);
        var (vm, card) = await LoadAsync(instance, 50);

        card.ToggleMembersCommand.Execute(null);
        Assert.True(card.MembersExpanded);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count == 50));

        Assert.True(vm.LocalFleets[0].MembersExpanded);
        Assert.Equal(50, vm.LocalFleets[0].VisibleMembers.Count);

        vm.Dispose();
    }

    // --- Where ET-52 and ET-53 meet -------------------------------------------------------------------------------

    /// <summary>The number on the fold line is a fact about the fleet, so it has to be right the moment the fleet
    /// changes — including when the change was made on another screen entirely (ET-52).</summary>
    [AvaloniaFact]
    public async Task RemovingAPilotElsewhere_CorrectsTheFoldLineAndTheTotal_AtOnce()
    {
        using var instance = CreateInstance(new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) });
        long fleetId = await SeedFleetAsync(instance, 50);
        var (vm, card) = await LoadAsync(instance, 50);
        Assert.Contains("+ 44 more", card.MoreMembersLabel);

        // Removed in fleet metrics, one screen over — the operator's ET-52 case, on a fifty-man card.
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
            "the card did not pick up the removal");
        Assert.Contains("+ 43 more", vm.LocalFleets[0].MoreMembersLabel);
        Assert.Equal("49 in fleet", vm.LocalFleets[0].MemberCountLabel);

        vm.Dispose();
    }

    // --- The rule, stated once ------------------------------------------------------------------------------------

    /// <summary>
    /// A card folds when its own leaf list is longer than the limit — one rule, not one per branch. What a card LISTS
    /// is untouched by this ticket: a client-only fleet's card is its whole roster (ET-46) and a server fleet's card
    /// is my own characters in it, because that roster belongs to someone else. On a server card the fold line
    /// therefore says "of yours", so its number can never be read as the fleet's size, which is on its own line.
    /// </summary>
    [AvaloniaFact]
    public void AServerCardsFoldLine_CountsMyCharacters_NotTheFleet()
    {
        var fleet = new FleetInfo(7, "Server Op", null, FleetVisibility.Public, FleetState.Active, Owner,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
        var local = new FleetViewModel(fleet, Owner, "Jithran");
        var server = new FleetViewModel(fleet, Owner, "Jithran", "server:1", "Test Server") { MemberCount = 40 };

        foreach (var row in new[] { local, server })
        {
            for (var i = 0; i < 9; i++)
                row.Members.Add(new FleetMemberRowViewModel(
                    i, FirstExternal + i, $"Pilot {i:00}", "Squad Member", null, null,
                    new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask)));
            row.RefreshVisibleMembers();
        }

        Assert.True(server.CanShortenMembers);
        Assert.Equal(6, server.VisibleMembers.Count);
        Assert.Equal("▾ + 3 more of yours", server.MoreMembersLabel);
        Assert.Equal("40 in fleet", server.MemberCountLabel);   // the fleet's size, separate from my nine leaves

        Assert.Equal("▾ + 3 more", local.MoreMembersLabel);      // a local card's list IS the roster, so no qualifier
    }
}
