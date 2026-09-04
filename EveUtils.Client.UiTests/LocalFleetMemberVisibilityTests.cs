using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-46: a character added to a client-only (local) fleet showed up in fleet manage but in neither the fleet
/// browser nor fleet metrics. Three screens read the same roster and disagreed about it:
/// <list type="bullet">
/// <item>the browser card built its member leaves only from characters coupled on THIS client, so an external
/// pilot — the one kind of member a local fleet can only add as external — was filtered out for good;</item>
/// <item>the browser's own ADD TOON / ADD EXTERNAL actions never reloaded, so the card did not even change;</item>
/// <item>fleet metrics takes its roster once, at construction, and the module host handed a second OPEN METRICS
/// back the module built before the pilot joined — an external pilot publishes no samples, so lazy discovery
/// could never fill the gap either.</item>
/// </list>
/// A member missing here is worse than a blank row: it silently shrinks the roll-up totals and the WITH FC badge's
/// denominator (ET-31), which is the figure an FC steers on.
/// </summary>
public class LocalFleetMemberVisibilityTests
{
    private const int Owner = 95000001;
    private const int Alt = 95000002;
    private const int External = 96000001;

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private static TestClientInstance CreateInstance(RecordingDialogService? dialogs = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Owner] = "Jithran",
                [Alt] = "Abnoba Auscent",
                [External] = "Nomad Pilot",
            });
            if (dialogs is not null)
                services.AddSingleton<IDialogService>(dialogs);
        });

    private static async Task SeedCharactersAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));
    }

    private static async Task<long> SeedLocalFleetAsync(TestClientInstance instance)
    {
        var created = await instance.Services.GetRequiredService<ClientFleetService>()
            .CreateLocalFleetAsync("Home Fleet", null, Owner);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static async Task<FleetsViewModel> LoadedFleetsAsync(TestClientInstance instance)
    {
        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 200 && vm.LocalFleets.Count == 0; i++)
            await Task.Delay(20);
        Assert.Single(vm.LocalFleets);
        return vm;
    }

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

    // --- The fleet browser -------------------------------------------------------------------------------------

    /// <summary>
    /// The local fleet's card IS that fleet's roster — its own ADD EXTERNAL button sits on it — so every member the
    /// roster holds belongs on it, external pilots included. The card used to list only members whose character is
    /// coupled on this client, which drops exactly the pilots you can add no other way.
    /// </summary>
    [AvaloniaFact]
    public async Task LocalFleetCard_ListsEveryRosterMember_IncludingAnExternalPilot()
    {
        using var instance = CreateInstance();
        await SeedCharactersAsync(instance);
        var fleetId = await SeedLocalFleetAsync(instance);

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        Assert.True((await fleets.AddLocalCharacterAsync(fleetId, Alt, Owner)).IsSuccess);
        Assert.True((await fleets.AddExternalAsync(fleetId, External, Owner)).IsSuccess);

        var vm = await LoadedFleetsAsync(instance);
        var card = vm.LocalFleets[0];
        await WaitForAsync(() => card.Members.Count >= 3);

        var ids = card.Members.Select(m => m.CharacterId).ToList();
        Assert.Contains(Owner, ids);
        Assert.Contains(Alt, ids);
        Assert.Contains(External, ids);

        // The external pilot is not coupled here, so their name comes from the same public lookup the roster and
        // the metrics screen use — never a bare id, and never a silently dropped row.
        Assert.Equal("Nomad Pilot", card.Members.Single(m => m.CharacterId == External).CharacterName);
    }

    /// <summary>
    /// And the same thing on screen, not only in the collection: the pilot's name has to be readable on the card in
    /// the window the operator actually looks at. A member-leaf that exists in the view-model but renders nowhere is
    /// the failure mode this screen family keeps producing (ET-30, ET-43).
    /// </summary>
    [AvaloniaFact]
    public async Task LocalFleetCard_RendersTheExternalPilotsName()
    {
        using var instance = CreateInstance(new RecordingDialogService());
        await SeedCharactersAsync(instance);
        var fleetId = await SeedLocalFleetAsync(instance);
        Assert.True((await instance.Services.GetRequiredService<ClientFleetService>()
            .AddExternalAsync(fleetId, External, Owner)).IsSuccess);

        var vm = await LoadedFleetsAsync(instance);
        await WaitForAsync(() => vm.LocalFleets[0].Members.Count >= 2);

        var window = new Views.FleetsWindow(vm) { Width = 760, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shown = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        Assert.Contains("Jithran", shown);        // the FC, who was never in doubt
        Assert.Contains("Nomad Pilot", shown);    // the external pilot the card used to drop

        Assert.NotNull(window.CaptureRenderedFrame()); // the card really painted; the names above are what it painted
        window.Close();
    }

    /// <summary>ADD EXTERNAL on the card has to change the card. It used to set a status line and stop.</summary>
    [AvaloniaFact]
    public async Task AddExternalPilot_ShowsOnTheCard_WithoutAManualRefresh()
    {
        var dialogs = new RecordingDialogService { OnAddExternalMember = _ => Task.FromResult<int?>(External) };
        using var instance = CreateInstance(dialogs);
        await SeedCharactersAsync(instance);
        await SeedLocalFleetAsync(instance);

        var vm = await LoadedFleetsAsync(instance);
        var card = vm.LocalFleets[0];
        Assert.DoesNotContain(External, card.Members.Select(m => m.CharacterId));

        await vm.AddLocalExternalCommand.ExecuteAsync(card);

        Assert.True(await WaitForAsync(() =>
                vm.LocalFleets[0].Members.Any(m => m.CharacterId == External)),
            "ADD EXTERNAL did not put the pilot on the card");
    }

    /// <summary>Same for ADD TOON: adding one of my own alts must land on the card straight away.</summary>
    [AvaloniaFact]
    public async Task AddLocalCharacter_ShowsOnTheCard_WithoutAManualRefresh()
    {
        var dialogs = new RecordingDialogService
        {
            OnPickCharacters = (_, _) => Task.FromResult<IReadOnlyList<int>?>([Alt]),
        };
        using var instance = CreateInstance(dialogs);
        await SeedCharactersAsync(instance);
        await SeedLocalFleetAsync(instance);

        var vm = await LoadedFleetsAsync(instance);
        var card = vm.LocalFleets[0];
        Assert.DoesNotContain(Alt, card.Members.Select(m => m.CharacterId));

        await vm.AddLocalCharacterCommand.ExecuteAsync(card);

        Assert.True(await WaitForAsync(() =>
                vm.LocalFleets[0].Members.Any(m => m.CharacterId == Alt)),
            "ADD TOON did not put the character on the card");
    }

    /// <summary>
    /// Widening the card to the whole roster must not widen the metric publish set with it. This client can publish
    /// for the characters signed in on it and for nobody else — an external pilot flies on their own machine, so
    /// registering them here would only add a per-second poll that can never produce a sample.
    /// </summary>
    [AvaloniaFact]
    public async Task ExternalPilotOnTheCard_IsNotRegisteredAsSomethingThisClientPublishesFor()
    {
        using var instance = CreateInstance();
        await SeedCharactersAsync(instance);
        var fleetId = await SeedLocalFleetAsync(instance);

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        Assert.True((await fleets.AddLocalCharacterAsync(fleetId, Alt, Owner)).IsSuccess);
        Assert.True((await fleets.AddExternalAsync(fleetId, External, Owner)).IsSuccess);
        // The question here is who this client publishes for, so the fleet has to be one that publishes at all: a
        // fleet is created Forming and only a started one contributes participation (ET-165).
        Assert.True((await fleets.StartFleetAsync(fleetId, Owner)).IsSuccess);

        var vm = await LoadedFleetsAsync(instance);
        await WaitForAsync(() => vm.LocalFleets[0].Members.Count >= 3);

        var participation = instance.Services.GetRequiredService<IFleetParticipation>();
        var ids = participation.Current.Where(p => p.FleetId == fleetId).Select(p => p.CharacterId).ToList();
        Assert.Contains(Owner, ids);
        Assert.Contains(Alt, ids);
        Assert.DoesNotContain(External, ids);
    }

    // --- Fleet metrics -----------------------------------------------------------------------------------------

    private static FleetInfo Op(long id, string name) => new(
        id, name, null, FleetVisibility.InviteOnly, FleetState.Active, Owner, null, null,
        DateTimeOffset.UnixEpoch, FleetActivation.Active);

    private static async Task<FleetMetricsViewModel> MetricsAsync(
        TestClientInstance instance, IFleetClient client, FleetInfo fleet, int expectedMembers)
    {
        var vm = new FleetMetricsViewModel(instance.Services, client, fleet);
        Assert.True(await WaitForAsync(() => vm.Members.Count >= expectedMembers),
            $"roster pre-fill stalled at {vm.Members.Count} of {expectedMembers}");
        return vm;
    }

    private static FleetMetricsViewModel HostedMetrics(FakeDisplay display) =>
        Assert.IsType<FleetMetricsViewModel>(Assert.Single(display.HostTabs).Content.DataContext);

    /// <summary>
    /// The screen an FC steers on may not keep showing a roster from before the pilot joined. Metrics reads its
    /// roster once, at construction; the module host de-duped on the window title alone, so a second OPEN METRICS
    /// re-selected that first instance and threw the freshly built one away. An external pilot never publishes a
    /// sample, so the lazy per-sample discovery could not repair it either — the pilot simply stayed missing.
    /// </summary>
    [AvaloniaFact]
    public async Task Metrics_ReopenedAfterAPilotJoined_ShowsThePilot()
    {
        using var instance = CreateInstance();
        var display = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());
        dialogs.SetHost(display);

        var fleet = Op(1, "Home Fleet");
        var client = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(1, Owner, -1, -1, FleetRole.FleetCommander, false)],
        };

        dialogs.ShowFleetMetrics(await MetricsAsync(instance, client, fleet, 1));
        Assert.Single(display.HostTabs);
        Assert.DoesNotContain(External, HostedMetrics(display).Members.Select(m => m.CharacterId));

        // The pilot joins in fleet manage, then the FC opens metrics again.
        client.Members =
        [
            new FleetMemberInfo(1, Owner, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, External, -1, -1, FleetRole.SquadMember, true),
        ];
        dialogs.ShowFleetMetrics(await MetricsAsync(instance, client, fleet, 2));

        Assert.Single(display.HostTabs);   // still one metrics module for this fleet, not a second tab
        Assert.True(await WaitForAsync(() =>
                HostedMetrics(display).Members.Any(m => m.CharacterId == External)),
            "re-opening fleet metrics kept the roster from before the pilot joined");
    }

    /// <summary>
    /// Same title-only de-dupe, second symptom: metrics for a second fleet re-selected the first fleet's screen.
    /// The roster window already carries a per-fleet module id for exactly this reason; metrics did not.
    /// </summary>
    [AvaloniaFact]
    public async Task Metrics_ForASecondFleet_GetsItsOwnModule()
    {
        using var instance = CreateInstance();
        var display = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());
        dialogs.SetHost(display);

        var alpha = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(1, Owner, -1, -1, FleetRole.FleetCommander, false)],
        };
        var bravo = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(2, Alt, -1, -1, FleetRole.FleetCommander, false)],
        };

        dialogs.ShowFleetMetrics(await MetricsAsync(instance, alpha, Op(1, "Alpha"), 1));
        dialogs.ShowFleetMetrics(await MetricsAsync(instance, bravo, Op(2, "Bravo"), 1));

        Assert.Equal(2, display.HostTabs.Count);
        var hosted = display.HostTabs
            .Select(t => Assert.IsType<FleetMetricsViewModel>(t.Content.DataContext).FleetName)
            .ToList();
        Assert.Contains("Alpha", hosted);
        Assert.Contains("Bravo", hosted);
    }
}
