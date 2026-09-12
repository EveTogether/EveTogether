using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-242: two pilots on one shared fleet run see each other's loot as it comes in — per character, with the items and
/// what they are worth — in the LOOT section. On a shared run loot and bounty are shared unless the pilot switches them
/// off on the window's face, and off means the other sees nothing more of it from that moment. Off a shared run nothing
/// about what is shared changes.
///
/// The two clients talk through <see cref="ServerWire"/>: serialized at the sender and read back by event type at the
/// receiver, the way the server hands a message on — so a payload the wire cannot carry fails here, not in a fleet.
/// </summary>
public sealed class FleetRunLootSharingTests
{
    private const long FleetId = 4242;
    private const string GroupCode = "HF-L00T";
    private const int JithranId = 90000001;
    private const int RaymondId = 90000002;
    private const int RaymondsSecondToonId = 90000003;
    private const int Tritanium = 34;

    [AvaloniaFact]
    public async Task OnASharedRun_OnePilotsLoot_ShowsLiveInTheOthersLootSection_WithItsItemsAndValue()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        await fleet.LootAsync(fleet.Jithran, quantity: 3);

        await fleet.SettleAsync(() => fleet.Raymond.SharedBlock(JithranId)?.Loot.NetIsk == 300m);

        ActivityLootCharacterViewModel block = Assert.Single(fleet.Raymond.Window.LootOverview!.FleetCharacters);
        Assert.Equal(JithranId, block.CharacterId);
        Assert.Equal("Jithran", block.CharacterText);
        Assert.True(block.IsSharedByFleet);
        Assert.Equal("live · 1 capture", block.SharedText);
        ActivityLootLineViewModel line = Assert.Single(block.Loot.ItemRows);
        Assert.Equal("Tritanium", line.Name);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(300m, line.Value);
        Assert.Equal(300m, block.Loot.NetIsk);
        Assert.False(block.Loot.CanCorrect);

        // Theirs is shown, never counted: Raymond's own group is only Raymond, and the figures above say so.
        Assert.DoesNotContain(fleet.Raymond.Window.LootOverview!.Characters, own => own.CharacterId == JithranId);
        Assert.Null(fleet.Raymond.Window.LootOverview!.NetIsk);
        Assert.True(fleet.Jithran.Window.FleetSharing.IsShown);
        Assert.True(fleet.Jithran.Window.FleetSharing.IsSharingLoot);
    }

    /// <summary>
    /// A burst of captures is one message on the wire, sent once the burst has settled — not one per capture — with the
    /// pilot's own count of what counts. Nothing new is nothing sent, until the resend a member who connected late
    /// relies on. Driven on a clock of its own, second by second, so the window cannot race it.
    /// </summary>
    [AvaloniaFact]
    public async Task ABurstOfCaptures_GoesOutAsOneShare_OnceItHasSettled()
    {
        ServerWire wire = new();
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<IRemoteEventTransport>(wire));
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(JithranId, FleetId, ClientOnly: false)]);
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        ActivityLootViewModel loot = new(() => new RunLootViewModel(dispatcher));
        ActivityLootCharacterViewModel block = loot.Show(Guid.NewGuid(), JithranId, "Jithran");
        RunFleetSharingViewModel sharing = new(instance.Services);
        DateTime start = DateTime.UtcNow;
        List<RunLootCaptureDto> captures = [];

        async Task CaptureAsync(long quantity, double atSecond)
        {
            captures.Add(new RunLootCaptureDto(Guid.NewGuid(), start.AddSeconds(atSecond), IsExcluded: false, ContentHash: null,
                LootCaptureSource.Clipboard, LootCaptureRole.Snapshot,
                [new RunLootEntryDto(Tritanium, "Tritanium", quantity, ClipboardPrice: null, LootKind.Gained)]));
            await block.Loot.LoadAsync(captures);
        }

        Task TickAsync(TimeSpan at) => sharing.SyncAsync(start + at, FleetId, GroupCode, isRunOpen: true, [JithranId], loot);

        await CaptureAsync(1, 0);
        await TickAsync(TimeSpan.Zero);
        await CaptureAsync(2, 0.5);
        await TickAsync(TimeSpan.FromSeconds(1));
        await CaptureAsync(4, 1.5);
        Assert.Empty(wire.Sent.OfType<FleetRunShareEvent>());

        await TickAsync(RunFleetSharingViewModel.BundleWindow);

        FleetRunShareEvent share = Assert.Single(wire.Sent.OfType<FleetRunShareEvent>());
        Assert.Equal(JithranId, share.CharacterId);
        Assert.Equal((FleetId, GroupCode, 3), (share.Data.FleetId, share.Data.GroupCode, share.Data.CaptureCount));
        Assert.Equal(new RunShareLootLine(Tritanium, 7, LootKind.Gained), Assert.Single(share.Data.Loot));

        await TickAsync(TimeSpan.FromSeconds(10));
        Assert.Single(wire.Sent.OfType<FleetRunShareEvent>());

        await TickAsync(RunFleetSharingViewModel.BundleWindow + RunFleetSharingViewModel.ResendInterval);
        Assert.Equal(2, wire.Sent.OfType<FleetRunShareEvent>().Count());
    }

    /// <summary>
    /// One click, and the other sees nothing more of it: the items leave the LOOT section and the figures leave the
    /// FLEET row at once — not after a bundle window, and not when a sample stops coming — and no later tick of the
    /// metric stream puts them back. Bounty behaves the same way on its own toggle.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingLootAndBountyOff_TakesThemOffTheOthersScreen_AtOnce()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        await fleet.LootAsync(fleet.Jithran, quantity: 3);
        await fleet.SettleAsync(() => fleet.Raymond.SharedBlock(JithranId)?.Loot.NetIsk == 300m);
        await fleet.Jithran.PublishMetricsAsync();
        await fleet.SettleAsync(() => fleet.Raymond.MemberRow(JithranId)?.LootIsk == 300m);

        // Shared by default on a shared run, although the global opt-in for both is off.
        Assert.Equal(300m, fleet.Raymond.MemberRow(JithranId)?.LootIsk);
        Assert.Equal(0m, fleet.Raymond.MemberRow(JithranId)?.BountyIsk);

        await fleet.Jithran.Window.FleetSharing.ToggleLootCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => fleet.Raymond.Window.LootOverview!.FleetCharacters.Count == 0);

        Assert.False(fleet.Jithran.Window.FleetSharing.IsSharingLoot);
        Assert.Empty(fleet.Raymond.Window.LootOverview!.FleetCharacters);
        Assert.Null(fleet.Raymond.MemberRow(JithranId)?.LootIsk);

        await fleet.Jithran.Window.FleetSharing.ToggleBountyCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => fleet.Raymond.MemberRow(JithranId) is null);
        await fleet.Jithran.PublishMetricsAsync();

        Assert.False(fleet.Jithran.Window.FleetSharing.IsSharingBounty);
        Assert.Null(fleet.Raymond.MemberRow(JithranId));
        Assert.DoesNotContain(fleet.Jithran.Wire.Sent.OfType<FleetMetricEvent>().Skip(fleet.Jithran.MetricsBeforeLastTick),
            metric => metric.Data.Kind is MetricKind.Loot or MetricKind.Bounty);
    }

    /// <summary>
    /// Raymond's second toon in the same fleet hears Raymond's own share come back over its own connection. Those are
    /// Raymond's own characters (ET-210), never fleet members — only the other pilot gets a block.
    /// </summary>
    [AvaloniaFact]
    public async Task AShareFromOneOfThisPilotsOwnCharacters_IsNeverShownAsAFleetMember()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        await fleet.Raymond.Instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Raymond Two", RaymondsSecondToonId));
        IEventBus bus = fleet.Raymond.Instance.Services.GetRequiredService<IEventBus>();
        // Later than anything the fleet's own windows send meanwhile, so the store keeps these two.
        RunShareUpdate share = new(FleetId, GroupCode, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            SharesLoot: true, SharesBounty: true, 1, [new RunShareLootLine(Tritanium, 5, LootKind.Gained)]);

        await bus.PublishAsync(new FleetRunShareEvent(share, RaymondsSecondToonId), EventTarget.Local);
        await bus.PublishAsync(new FleetRunShareEvent(share, JithranId), EventTarget.Local);
        await fleet.SettleAsync(() => fleet.Raymond.Window.LootOverview!.FleetCharacters.Count == 1);

        Assert.Equal(JithranId, Assert.Single(fleet.Raymond.Window.LootOverview!.FleetCharacters).CharacterId);
    }

    /// <summary>
    /// Off a shared run the opt-in stands exactly as it did: loot and bounty stay private until turned on, globally or
    /// for the fleet. On one, the run's own choice comes first, then the fleet's, and without either it is shared.
    /// </summary>
    [Theory]
    [InlineData(false, null, null, null, false)]    // no shared run, nothing chosen: private, as before
    [InlineData(false, "true", null, null, true)]   // no shared run, shared globally
    [InlineData(false, null, "true", null, true)]   // no shared run, shared with this fleet
    [InlineData(true, null, null, null, true)]      // a shared run, nothing chosen: shared, and the window says so
    [InlineData(true, "false", null, null, true)]   // the global opt-out is not consulted on a shared run
    [InlineData(true, null, "false", null, false)]  // the fleet's own "never" holds on its runs
    [InlineData(true, null, "false", "true", true)] // …until this run is switched on
    [InlineData(true, "true", "true", "false", false)] // the run's own "off" beats everything else
    public void WhoSeesLootAndBounty_FollowsTheRunThenTheFleetThenTheGlobalChoice(
        bool onSharedRun, string? global, string? fleetOverride, string? runChoice, bool expected)
    {
        Dictionary<string, string> values = [];
        foreach (MetricKind kind in new[] { MetricKind.Loot, MetricKind.Bounty })
        {
            if (global is not null)
                values[MetricShareSnapshot.KeyFor(kind)] = global;
            if (fleetOverride is not null)
                values[MetricShareSnapshot.OverrideKeyFor(FleetId, JithranId, kind)] = fleetOverride;
            if (runChoice is not null)
                values[MetricShareSnapshot.RunKeyFor(GroupCode, kind)] = runChoice;
        }

        MetricShareSnapshot snapshot = new(values,
            onSharedRun ? new Dictionary<(long FleetId, int CharacterId), string> { [(FleetId, JithranId)] = GroupCode } : null);

        Assert.Equal(expected, snapshot.IsShared(FleetId, JithranId, MetricKind.Loot));
        Assert.Equal(expected, snapshot.IsShared(FleetId, JithranId, MetricKind.Bounty));
        // Nothing else is the run's to decide: location stays on its own opt-in.
        Assert.False(snapshot.IsShared(FleetId, JithranId, MetricKind.Location));
    }

    /// <summary>A window on no run, in the same fleet, shows no toggle and puts nobody on a shared run; closing a shared
    /// run's window takes its pilot off it, so the next tick is back on the fleet and global choices.</summary>
    [AvaloniaFact]
    public async Task OnlyAnOpenSharedRunWindow_PutsItsPilotOnTheSharedRun()
    {
        using (TestClientInstance instance = TestClientInstance.Create())
        {
            instance.Services.GetRequiredService<IFleetParticipation>()
                .Set([new FleetParticipant(JithranId, FleetId, ClientOnly: false)]);
            using ActivityWindowViewModel idle = new(ActivityKind.Site, instance.Services);
            await idle.LoadAsync();
            idle.Refresh(DateTime.UtcNow);
            await FleetOfTwo.RunJobsAsync();

            Assert.False(idle.FleetSharing.IsShown);
            Assert.Empty(instance.Services.GetRequiredService<SharedFleetRuns>().Current);
        }

        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        SharedFleetRuns runs = fleet.Jithran.Instance.Services.GetRequiredService<SharedFleetRuns>();
        Assert.Equal(GroupCode, runs.Current.GetValueOrDefault((FleetId, JithranId)));

        fleet.Jithran.Window.Dispose();
        Assert.Empty(runs.Current);
    }

    [Fact]
    public void TheFleetsSharingDialog_OffersLoot_AsItsOwnChoice()
    {
        FleetShareViewModel dialog = new("Tuesday op", FleetId, [(JithranId, "Jithran")],
            new MetricShareSnapshot(new Dictionary<string, string>()));
        dialog.AllCharacters.Metrics.Single(row => row.Kind == MetricKind.Loot).ChoiceIndex = 2;

        Assert.Contains(dialog.BuildOverrides(), write =>
            write.Key == MetricShareSnapshot.OverrideKeyFor(FleetId, JithranId, MetricKind.Loot) && write.Value == "false");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class FleetOfTwo : IDisposable
    {
        private FleetOfTwo(Pilot jithran, Pilot raymond)
        {
            Jithran = jithran;
            Raymond = raymond;
        }

        public Pilot Jithran { get; }

        public Pilot Raymond { get; }

        public static async Task<FleetOfTwo> CreateAsync()
        {
            Pilot jithran = await Pilot.CreateAsync(JithranId, "Jithran");
            Pilot raymond = await Pilot.CreateAsync(RaymondId, "Raymond");
            jithran.Wire.Destinations.Add(raymond.Instance.Services);
            raymond.Wire.Destinations.Add(jithran.Instance.Services);

            RunGroupCodeStart start = new(FleetId, ActivityKind.Site, GroupCode, DateTime.UtcNow.AddMinutes(-2), IsFleetCommander: true);
            await jithran.JoinAsync(start);
            await raymond.JoinAsync(start);
            FleetOfTwo fleet = new(jithran, raymond);
            await fleet.SettleAsync(() => jithran.Window.FleetSharing.IsShown && raymond.Window.FleetSharing.IsShown);
            return fleet;
        }

        /// <summary>One capture on the pilot's own run, as the clipboard files it; the window's clock then runs past the
        /// bundle window, so it goes out.</summary>
        public async Task LootAsync(Pilot pilot, long quantity)
        {
            int capturesBefore = pilot.Window.LootOverview!.Characters.Sum(block => block.Loot.Captures.Count);
            await pilot.Instance.Services.GetRequiredService<IDispatcher>().Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
            {
                CapturedAtUtc = DateTime.UtcNow,
                Source = LootCaptureSource.Clipboard,
                CharacterId = pilot.CharacterId,
                PreferredRunId = pilot.Window.RunId,
                Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
            }));
            await SettleAsync(() => pilot.Window.LootOverview!.Characters.Sum(block => block.Loot.Captures.Count) > capturesBefore);
            await pilot.TickPastTheBundleWindowAsync();
        }

        public static async Task RunJobsAsync()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        }

        public async Task SettleAsync(Func<bool> until)
        {
            for (int attempt = 0; attempt < 100 && !until(); attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Jithran.Dispose();
            Raymond.Dispose();
        }
    }

    private sealed class Pilot : IDisposable
    {
        private DateTime _clock = DateTime.UtcNow;

        private Pilot(int characterId, TestClientInstance instance, ServerWire wire, ActivityWindowViewModel window)
        {
            CharacterId = characterId;
            Instance = instance;
            Wire = wire;
            Window = window;
        }

        public int CharacterId { get; }

        public TestClientInstance Instance { get; }

        public ServerWire Wire { get; }

        public ActivityWindowViewModel Window { get; }

        public int MetricsBeforeLastTick { get; private set; }

        public static async Task<Pilot> CreateAsync(int characterId, string name)
        {
            ServerWire wire = new();
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteEventTransport>(wire);
                services.AddSingleton<IDialogService>(new RecordingDialogService());
                services.AddSingleton<IToastService>(new RecordingToastService());
                services.AddSingleton<ILocalCharacterPresence>(new ActivityWindowHarness.StubPresence(true));
                services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(Tritanium, "Tritanium", 18, 4));
                services.AddSingleton<IExternalCharacterLookup>(
                    new FakeExternalLookup { [JithranId] = "Jithran", [RaymondId] = "Raymond" });
            });
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));
            await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [
                new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }
            ]);
            instance.Services.GetRequiredService<IFleetParticipation>()
                .Set([new FleetParticipant(characterId, FleetId, ClientOnly: false, JithranId, "fleet.example")]);
            _ = instance.Services.GetRequiredService<FleetRunShares>();

            ActivityWindowViewModel window = new(ActivityKind.Site, instance.Services);
            await window.LoadAsync();
            return new Pilot(characterId, instance, wire, window);
        }

        public async Task JoinAsync(RunGroupCodeStart start)
        {
            Window.JoinFleetRun(start);
            for (int attempt = 0; attempt < 100 && Window.RunId is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Window.Refresh(_clock);
        }

        /// <summary>The window's clock, ticked past the bundle window the way the real one-second timer would.</summary>
        public async Task TickPastTheBundleWindowAsync()
        {
            int sentBefore = Wire.Sent.Count(sent => sent is FleetRunShareEvent);
            for (int tick = 0; tick < 6 && Wire.Sent.Count(sent => sent is FleetRunShareEvent) == sentBefore; tick++)
            {
                _clock = _clock.AddSeconds(1);
                Window.Refresh(_clock);
                await FleetOfTwo.RunJobsAsync();
            }
        }

        /// <summary>One tick of the 1 Hz fleet metric stream, as the app's own publisher runs it.</summary>
        public async Task PublishMetricsAsync()
        {
            Window.Refresh(_clock);
            MetricsBeforeLastTick = Wire.Sent.OfType<FleetMetricEvent>().Count();
            await Instance.Services.GetRequiredService<FleetMetricPublisher>()
                .PublishTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public ActivityLootCharacterViewModel? SharedBlock(int characterId) =>
            Window.LootOverview?.FleetCharacters.FirstOrDefault(block => block.CharacterId == characterId);

        public ActivityFleetMemberViewModel? MemberRow(int characterId) =>
            Window.FleetMembers.FirstOrDefault(member => member.CharacterId == characterId);

        public void Dispose()
        {
            Window.Dispose();
            Instance.Dispose();
        }
    }

    /// <summary>What the server does with a fleet event on its way through: serialized at the sender, read back by its
    /// type at the receiver and attributed to the character that sent it. A type the receiver has no reader for is
    /// dropped, exactly as it is there.</summary>
    private sealed class ServerWire : IRemoteEventTransport
    {
        public List<IServiceProvider> Destinations { get; } = [];

        public List<IIntegrationEvent> Sent { get; } = [];

        public async Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            Sent.Add(integrationEvent);
            string payloadJson = integrationEvent.Data is null
                ? "{}"
                : JsonSerializer.Serialize(integrationEvent.Data, integrationEvent.Data.GetType());
            foreach (IServiceProvider destination in Destinations)
            {
                IIntegrationEvent? arrived = destination.GetRequiredService<IEventTypeRegistry>()
                    .Deserialize(integrationEvent.EventType, payloadJson, integrationEvent.CharacterId);
                if (arrived is not null)
                    await destination.GetRequiredService<IEventBus>().PublishAsync(arrived, EventTarget.Local, cancellationToken);
            }
        }
    }
}
