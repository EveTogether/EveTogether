using System;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// One character coupled on two machines (ET-440): the one playing it and one that only has it signed in. Both
/// publish every second; receivers flipped between "Amarr" and "offline" until the relay chose one.
/// </summary>
public class FleetPresenceSourceGuardTests
{
    private const long FleetId = 26;
    private const int Jithran = 90250177;
    private const string Playing = "playing-pc";
    private const string Idle = "idle-pc";

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 19, 0, 0, TimeSpan.Zero);

    private static MetricSample Presence(PresenceState state, long fleetId = FleetId) =>
        new(Jithran, fleetId, MetricKind.Presence, (double)state, 0);

    private static MetricSample Location(string system) =>
        new(Jithran, FleetId, MetricKind.Location, 30002187, 0, system);

    [Fact]
    public void IdleSource_WhileAnotherPlays_IsNotRelayed()
    {
        FleetPresenceSourceGuard guard = new();
        guard.Admit(Playing, Presence(PresenceState.InGame), Now);

        Assert.False(guard.Admit(Idle, Presence(PresenceState.NotInGame), Now.AddSeconds(1)));
        Assert.False(guard.Admit(Idle, Location("Amarr"), Now.AddSeconds(1)));
        Assert.True(guard.Admit(Playing, Location("Amarr"), Now.AddSeconds(1)));
    }

    [Fact]
    public void IdleSource_AfterThePlayingOneFallsQuiet_TakesOver()
    {
        FleetPresenceSourceGuard guard = new();
        guard.Admit(Playing, Presence(PresenceState.InGame), Now);

        Assert.True(guard.Admit(Idle, Presence(PresenceState.NotInGame), Now + FleetPresenceSourceGuard.HoldFor));
    }

    [Fact]
    public void SecondSource_ThatAlsoSeesTheGame_TakesTheClaim()
    {
        FleetPresenceSourceGuard guard = new();
        guard.Admit(Playing, Presence(PresenceState.InGame), Now);

        Assert.True(guard.Admit(Idle, Presence(PresenceState.InGame), Now.AddSeconds(1)));
        Assert.False(guard.Admit(Playing, Location("Amarr"), Now.AddSeconds(2)));
    }

    [Fact]
    public void SourcesWithoutAnInGameClaim_AreRelayedAsBefore()
    {
        FleetPresenceSourceGuard guard = new();

        Assert.True(guard.Admit(Idle, Location("Amarr"), Now));
        Assert.True(guard.Admit(Playing, Presence(PresenceState.NotInGame), Now));
        Assert.True(guard.Admit(Idle, Presence(PresenceState.Unknown), Now));
    }

    [Fact]
    public void Claim_InOneFleet_LeavesAnotherFleetAlone()
    {
        FleetPresenceSourceGuard guard = new();
        guard.Admit(Playing, Presence(PresenceState.InGame), Now);

        Assert.True(guard.Admit(Idle, Presence(PresenceState.NotInGame, fleetId: FleetId + 1), Now.AddSeconds(1)));
    }
}
