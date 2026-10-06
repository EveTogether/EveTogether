using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The event bus stream drops an event whose type its registry does not know instead of failing (ET-458), so a client
/// and a server of different versions can sit on one fleet while a newer event type rolls out.
/// </summary>
public sealed class UnknownWireEventTests
{
    private static readonly string SavedPayload = JsonSerializer.Serialize(
        new RunGroupSave(4242, ActivityKind.Site, "HF-L00T", DateTime.UtcNow));

    [Fact]
    public void Deserialize_TypeNobodyRegistered_IsNullNotAnError()
    {
        EventTypeRegistry registry = new();
        new FleetWireEvents().RegisterInto(registry);

        Assert.Null(registry.Deserialize("fleet.run-from-a-newer-version", "{}", null));
    }

    [Fact]
    public void Deserialize_FleetRunSaved_ReadsTheCommandersSave()
    {
        EventTypeRegistry registry = new();
        new FleetWireEvents().RegisterInto(registry);

        FleetRunSavedEvent saved = Assert.IsType<FleetRunSavedEvent>(registry.Deserialize("fleet.run-saved", SavedPayload, 90000001));

        Assert.Equal("HF-L00T", saved.Data.GroupCode);
        Assert.Equal(4242, saved.FleetId);
    }

    [Fact]
    public void Deserialize_ServerWithoutTheSavedEvent_DropsIt()
    {
        Assert.Null(new EventTypeRegistry().Deserialize("fleet.run-saved", SavedPayload, 90000001));
    }
}
