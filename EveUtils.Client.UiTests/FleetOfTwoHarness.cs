using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

namespace EveUtils.Client.UiTests;

/// <summary>
/// Two pilots on one shared fleet run, talking through a fake server wire — the harness <see cref="FleetRunLootSharingTests"/>
/// (ET-242) and <see cref="FleetRunMiningSharingTests"/> (ET-234) both drive, since both exercise the same shared-run
/// sharing mechanics (<c>RunFleetSharingViewModel</c>, <c>FleetRunShares</c>) on two different figures.
/// </summary>
internal sealed class FleetOfTwo : IDisposable
{
    public const long FleetId = 4242;
    public const string GroupCode = "HF-L00T";
    public const int JithranId = 90000001;
    public const int RaymondId = 90000002;
    public const int RaymondsSecondToonId = 90000003;
    public const int Tritanium = 34;

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

    /// <summary>One ore aggregated onto the pilot's own run (ET-229), as the gamelog watcher would file it; the
    /// window's clock then runs past the bundle window, so it goes out (ET-234).</summary>
    public async Task MineAsync(Pilot pilot, string oreType, int units, int residueUnits = 0)
    {
        int rowsBefore = pilot.Window.Participants.Sum(participant => participant.MiningEntries.Count);
        await pilot.Instance.Services.GetRequiredService<IDispatcher>().Send(
            new AddRunMiningEntryCommand(pilot.CharacterId, DateTime.UtcNow, oreType, units, IsCritical: false, residueUnits));
        await SettleAsync(() => pilot.Window.Participants.Sum(participant => participant.MiningEntries.Count) >= rowsBefore);
        pilot.Refresh();
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

internal sealed class Pilot : IDisposable
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
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(FleetOfTwo.Tritanium, "Tritanium", 18, 4));
            services.AddSingleton<IExternalCharacterLookup>(
                new FakeExternalLookup { [FleetOfTwo.JithranId] = "Jithran", [FleetOfTwo.RaymondId] = "Raymond" });
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, characterId));
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = FleetOfTwo.Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }
        ]);
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(characterId, FleetOfTwo.FleetId, ClientOnly: false, FleetOfTwo.JithranId, "fleet.example")]);
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

    /// <summary>Re-reads this pilot's own window against its current clock, for a caller that changed something the
    /// window has to notice before the next tick (ET-234's mining totals).</summary>
    public void Refresh() => Window.Refresh(_clock);

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
internal sealed class ServerWire : IRemoteEventTransport
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
