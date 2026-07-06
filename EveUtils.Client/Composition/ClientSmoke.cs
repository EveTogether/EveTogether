using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Commands;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Queries;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Ships.Commands;
using EveUtils.Shared.Modules.Ships.Events;
using EveUtils.Shared.Modules.Ships.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>Headless verification of the client data/CQRS layer (via <c>--smoke</c>).</summary>
public static class ClientSmoke
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        // Local event bus: simple (synchronous) listener on the internal event that
        // AddShipCommandHandler publishes itself — this is how window B would refresh its datagrid.
        var received = new List<string>();
        using var subscription = eventBus.Subscribe<ShipAddedEvent>(evt =>
            received.Add($"#{evt.Data.Id} {evt.Data.Name} ({evt.Data.Class}) " +
                         $"char={evt.CharacterId?.ToString() ?? "system"} @ {evt.Timestamp:HH:mm:ss}"));

        var ships = await dispatcher.Query(new GetShipsQuery());
        if (ships.Count == 0)
        {
            await dispatcher.Send(new AddShipCommand("Rifter", "Frigate", 1_067_000m));
            await dispatcher.Send(new SetSettingCommand("theme", "dark"));
            ships = await dispatcher.Query(new GetShipsQuery());
        }

        // Gamelog: record a couple of owner-stamped combat hits through the gated dispatcher, then
        // read them back (the query scopes by the current principal's OwnerId — foundation pillar 4).
        var recordedTo = await dispatcher.Send(new RecordCombatCommand(
            CharacterId: null, Amount: 250, Direction: DamageDirection.Outgoing, Target: "Guristas Scout", At: DateTimeOffset.UtcNow));
        await dispatcher.Send(new RecordCombatCommand(
            CharacterId: null, Amount: 80, Direction: DamageDirection.Incoming, Target: "Guristas Scout", At: DateTimeOffset.UtcNow));
        var recentCombat = await dispatcher.Query(new GetRecentCombatQuery(5));

        Console.WriteLine("== EVE-Utils client smoke (vertical-slice CQRS + IDbContextFactory + event-bus) ==");
        foreach (var ship in ships)
            Console.WriteLine($"  ship #{ship.Id} {ship.Name} ({ship.Class})");
        foreach (var setting in await dispatcher.Query(new GetSettingsQuery()))
            Console.WriteLine($"  setting {setting.Key}={setting.Value}");
        foreach (var evt in received)
            Console.WriteLine($"  event ShipAdded → {evt}");
        Console.WriteLine($"  gamelog record gated-dispatch ok={recordedTo.IsSuccess}; recent owner-scoped samples:");
        foreach (var hit in recentCombat)
            Console.WriteLine($"    combat #{hit.Id} {hit.Amount} {hit.Direction} → {hit.Target} @ {hit.At:HH:mm:ss}");
    }
}
