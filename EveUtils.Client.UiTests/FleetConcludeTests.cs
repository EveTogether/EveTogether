using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless UI verification for the Conclude lifecycle (2026-06-04). Drives the real client DI in-process: a fleet
/// is opened in the actual <see cref="FleetRosterViewModel"/> the Manage window uses, and the owner's START/CONCLUDE
/// button visibility (CanStart/CanConclude) + activation label are asserted across Forming → Active → Concluded.
/// The real conclude path (LocalFleetClient → Shared ConcludeFleetCommand) is then exercised and the terminal state
/// is verified (no re-start).
/// </summary>
public class FleetConcludeTests
{
    private const int Owner = 95000042;
    private const int Member = 95000077;

    private static FleetInfo InfoFor(FleetEntity fleet, FleetActivation activation) => new(
        fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
        fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, activation);

    [AvaloniaFact]
    public async Task RosterButtons_TrackActivation_AndConcludeIsTerminal()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("UI conclude test", null, Owner);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;
        var fleet = await repository.GetAsync(fleetId);
        Assert.NotNull(fleet);

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);

        // Forming: the owner can START but not CONCLUDE (a never-started fleet is cancelled via Disband, not concluded).
        var forming = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Forming), isOwner: true, Owner);
        Assert.Equal("Forming", forming.ActivationLabel);
        Assert.True(forming.CanStart);
        Assert.False(forming.CanConclude);

        // Active: START hides (already started), CONCLUDE becomes available.
        var active = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Active), isOwner: true, Owner);
        Assert.Equal("Active", active.ActivationLabel);
        Assert.False(active.CanStart);
        Assert.True(active.CanConclude);

        // A non-owner never sees either management action.
        var asMember = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Active), isOwner: false, Owner);
        Assert.False(asMember.CanStart);
        Assert.False(asMember.CanConclude);

        // Concluded: both hide — it is terminal.
        var concluded = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Concluded), isOwner: true, Owner);
        Assert.Equal("Concluded", concluded.ActivationLabel);
        Assert.False(concluded.CanStart);
        Assert.False(concluded.CanConclude);

        // Real conclude path (the CONCLUDE button calls this seam): start, then conclude, and verify it sticks + is terminal.
        Assert.True((await client.StartFleetAsync(fleetId)).Ok);
        Assert.True((await client.ConcludeFleetAsync(fleetId)).Ok);

        var after = await repository.GetAsync(fleetId);
        Assert.Equal(FleetActivation.Concluded, after!.Activation);
        Assert.Equal(FleetState.Active, after.State); // concluded is kept for history, not archived

        Assert.False((await client.StartFleetAsync(fleetId)).Ok); // a concluded fleet cannot be started again
    }

    [AvaloniaFact]
    public async Task LocalFleetWithMember_StartAndConclude_DoNotCrash_OnServerOnlyMessageQueue()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("Local notify test", null, Owner);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;

        // A non-owner, non-external member is exactly who Start/Conclude notify through the message queue — which is
        // server-only (QueuedMessage lives in ServerDbContext, not the client). Without the host-guard in
        // EnqueueMessageCommandHandler this throws "Cannot create a DbSet for 'QueuedMessage'" on the client.
        // Regression for the local-only multi-member fleet crash.
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = Member,
            Role = FleetRole.SquadMember,
            IsExternal = false
        });

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);

        var started = await client.StartFleetAsync(fleetId);
        Assert.True(started.Ok, started.Message);

        var concluded = await client.ConcludeFleetAsync(fleetId);
        Assert.True(concluded.Ok, concluded.Message);

        var after = await repository.GetAsync(fleetId);
        Assert.Equal(FleetActivation.Concluded, after!.Activation);
    }

    [AvaloniaFact]
    public async Task RosterWindow_Renders_StartAndConcludeButtons_ThenConcludedState()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("Render conclude test", null, Owner);
        var fleetId = created.Value;
        var fleet = await repository.GetAsync(fleetId);
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);

        // Forming: the management header shows START for the owner (CONCLUDE only appears once the fleet is Active).
        var forming = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Forming), isOwner: true, Owner);
        for (var i = 0; i < 100 && forming.Tree.Count == 0; i++)
            await Task.Delay(50);
        var formingWindow = new FleetRosterWindow(forming) { Width = 900, Height = 600 };
        formingWindow.Show();
        var formingFrame = formingWindow.CaptureRenderedFrame();
        Assert.NotNull(formingFrame);
        formingFrame!.Save("/tmp/eveutils-conclude-forming.png");
        formingWindow.Close();

        // Concluded: the header shows the "Concluded" label and neither action button.
        var concluded = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Concluded), isOwner: true, Owner);
        for (var i = 0; i < 100 && concluded.Tree.Count == 0; i++)
            await Task.Delay(50);
        var concludedWindow = new FleetRosterWindow(concluded) { Width = 900, Height = 600 };
        concludedWindow.Show();
        var concludedFrame = concludedWindow.CaptureRenderedFrame();
        Assert.NotNull(concludedFrame);
        concludedFrame!.Save("/tmp/eveutils-conclude-concluded.png");
        concludedWindow.Close();
    }
}
