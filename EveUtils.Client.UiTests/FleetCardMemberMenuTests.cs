using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-44 on the surface ET-46 opened up. The fleet browser's card for a client-only fleet lists that fleet's WHOLE
/// roster since ET-46 — externals included — and an external pilot has no other row anywhere in that window, so the
/// shared member menu belongs on those leaves too: the pilot summary for everyone, and the removal for the fleet's
/// owner. A server fleet's card stays the "my characters" list it was designed as, where leaving is the pilot's own
/// LEAVE and removing someone else is not this card's business.
/// </summary>
public class FleetCardMemberMenuTests
{
    private const int Owner = 95000001;
    private const int Alt = 95000002;
    private const int External = 96000001;

    private static TestClientInstance CreateInstance(RecordingDialogService dialogs) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Owner] = "Jithran",
                [Alt] = "Abnoba Auscent",
                [External] = "Nomad Pilot",
            });
            services.AddSingleton<IDialogService>(dialogs);
        });

    private static RecordingDialogService AlwaysConfirms() =>
        new() { OnConfirm = (_, _) => Task.FromResult(true) };

    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 200)
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

    // A local fleet owned by Owner, holding the owner, one of my alts and an external pilot.
    private static async Task<(FleetsViewModel Vm, long FleetId)> LoadedCardAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        long fleetId = (await fleets.CreateLocalFleetAsync("Home Fleet", null, Owner)).Value;
        Assert.True((await fleets.AddLocalCharacterAsync(fleetId, Alt, Owner)).IsSuccess);
        Assert.True((await fleets.AddExternalAsync(fleetId, External, Owner)).IsSuccess);

        var vm = new FleetsViewModel(instance.Services);
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count >= 3));
        return (vm, fleetId);
    }

    private static FleetMemberRowViewModel Leaf(FleetsViewModel vm, int characterId) =>
        vm.LocalFleets[0].Members.Single(m => m.CharacterId == characterId);

    /// <summary>The same block, from the same builder, on the card — including the external pilot ET-46 restored.</summary>
    [AvaloniaFact]
    public async Task LocalFleetCard_CarriesTheSharedMemberMenu_OnEveryMemberIncludingTheExternal()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        var (vm, _) = await LoadedCardAsync(instance);

        var labels = Leaf(vm, External).MemberMenu.Select(i => i.Label).ToList();

        Assert.Contains("Nomad Pilot", labels);
        Assert.Contains("Squad Member · external pilot", labels);
        Assert.Contains("No fit assigned", labels);
        Assert.Contains(labels, l => l.StartsWith("Remove Nomad Pilot", StringComparison.Ordinal));

        // This card has no metric stream of its own, so it says so rather than inventing a sample age.
        Assert.Contains("Live metrics aren't tracked on this screen", labels);
    }

    // The fleet keeps its owner until ownership is handed on, so the creator's own leaf carries information only.
    [AvaloniaFact]
    public async Task LocalFleetCard_OffersNoRemoval_OnTheOwnersOwnRow()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        var (vm, _) = await LoadedCardAsync(instance);

        var labels = Leaf(vm, Owner).MemberMenu.Select(i => i.Label).ToList();

        Assert.Contains("Jithran", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("Remove ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Removing from the card runs the one shared flow — one confirmation naming the pilot, and no in-game question
    /// because a client-only fleet is not coupled — and the card then shows the roster it now has, the same way
    /// ADD TOON and ADD EXTERNAL reload it (ET-46).
    /// </summary>
    [AvaloniaFact]
    public async Task LocalFleetCard_Remove_TakesThePilotOffTheFleetAndOffTheCard()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var (vm, fleetId) = await LoadedCardAsync(instance);

        var remove = Assert.IsAssignableFrom<IAsyncRelayCommand>(
            Leaf(vm, External).MemberMenu.Single(i => i.Label.StartsWith("Remove ", StringComparison.Ordinal)).Command);
        await remove.ExecuteAsync(null);

        // Gone from the fleet itself…
        var members = await instance.Services.GetRequiredService<IFleetRepository>().ListMembersAsync(fleetId);
        Assert.DoesNotContain(External, members.Select(m => m.CharacterId));

        // …and gone from the card, without a manual refresh.
        Assert.True(await WaitForAsync(() => vm.LocalFleets[0].Members.All(m => m.CharacterId != External)));

        var (title, message) = Assert.Single(dialogs.ConfirmPrompts);
        Assert.Equal("Remove from fleet", title);
        Assert.Contains("Nomad Pilot", message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task LocalFleetCard_Remove_KeepsThePilot_WhenTheConfirmationIsDeclined()
    {
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(false) };
        using var instance = CreateInstance(dialogs);
        var (vm, fleetId) = await LoadedCardAsync(instance);

        var remove = Assert.IsAssignableFrom<IAsyncRelayCommand>(
            Leaf(vm, External).MemberMenu.Single(i => i.Label.StartsWith("Remove ", StringComparison.Ordinal)).Command);
        await remove.ExecuteAsync(null);

        var members = await instance.Services.GetRequiredService<IFleetRepository>().ListMembersAsync(fleetId);
        Assert.Contains(External, members.Select(m => m.CharacterId));
        Assert.Contains(External, vm.LocalFleets[0].Members.Select(m => m.CharacterId));
    }

    /// <summary>
    /// And it is really mounted on the rendered card, not just present on the view-model: the leaf's own
    /// <c>ContextMenu</c> has to carry the row's menu and the app-level item theme. A menu that resolved neither
    /// renders as an empty popup, which looks exactly like a menu that is simply closed (ET-30, ET-43).
    /// </summary>
    [AvaloniaFact]
    public async Task LocalFleetCard_MountsTheMenuOnTheRenderedLeaf()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        var (vm, _) = await LoadedCardAsync(instance);

        var window = new Views.FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The leaf grid that stands for the external pilot, found by the name it paints.
        Control leaf = window.GetVisualDescendants().OfType<Grid>()
            .First(g => g.ContextMenu is not null
                        && g.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Nomad Pilot"));

        ContextMenu menu = leaf.ContextMenu!;

        // Showing a context menu parents it to its control, which is what lets its binding see the row; headless has
        // no popup surface, so take that one step by hand (same as FleetMemberMenuTests).
        ((ISetLogicalParent)menu).SetParent(leaf);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(menu.ItemContainerTheme);
        var labels = Assert.IsAssignableFrom<IEnumerable<FleetMemberMenuItemViewModel>>(menu.ItemsSource)
            .Select(i => i.Label)
            .ToList();
        Assert.Contains("Squad Member · external pilot", labels);

        window.Close();
    }
}
