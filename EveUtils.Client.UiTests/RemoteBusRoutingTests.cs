using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

using Ref = RemoteBusConnectionManager.ConnectionRef;

/// <summary>
/// Outbound event routing (anti-spoof, EventBusStreamService): the server attributes every event to the attached
/// session character and rejects one published over another character's stream. A character-stamped event must
/// therefore travel over THAT character's connection — multiboxing several characters must not funnel all their
/// metrics through one stream (the bug that left two of a player's characters with no DPS/bounty/location).
/// </summary>
public class RemoteBusRoutingTests
{
    private const string Server = "srv:7443";
    private const int Catbank = 90250177;
    private const int RaymondKrah = 2122696898;

    [Fact]
    public void CharacterStampedEvent_GoesOnlyOverThatCharactersConnection()
    {
        IReadOnlyList<Ref> connected = [new(Server, Catbank), new(Server, RaymondKrah)];

        var targets = RemoteBusConnectionManager.SelectTargets(connected, claimedCharacter: RaymondKrah);

        var only = Assert.Single(targets);
        Assert.Equal(RaymondKrah, only.CharacterId);   // not Catbank's stream → server no longer rejects it
    }

    [Fact]
    public void EveryMultiboxedCharacter_RoutesToItsOwnStream_NotJustTheFirst()
    {
        IReadOnlyList<Ref> connected = [new(Server, Catbank), new(Server, RaymondKrah)];

        Assert.Equal(Catbank, Assert.Single(RemoteBusConnectionManager.SelectTargets(connected, Catbank)).CharacterId);
        Assert.Equal(RaymondKrah, Assert.Single(RemoteBusConnectionManager.SelectTargets(connected, RaymondKrah)).CharacterId);
    }

    [Fact]
    public void CharacterAgnosticEvent_DedupesToOneConnectionPerServer()
    {
        IReadOnlyList<Ref> connected = [new(Server, Catbank), new(Server, RaymondKrah)];

        // CharacterId 0 → one stream per server is enough (sending up both would reroute the event twice).
        var targets = RemoteBusConnectionManager.SelectTargets(connected, claimedCharacter: 0);
        Assert.Single(targets);
        Assert.Equal(Server, targets[0].ServerAddress);
    }

    [Fact]
    public void CharacterAgnosticEvent_AcrossServers_KeepsOnePerServer()
    {
        const string other = "srv:8443";
        IReadOnlyList<Ref> connected = [new(Server, Catbank), new(Server, RaymondKrah), new(other, Catbank)];

        var targets = RemoteBusConnectionManager.SelectTargets(connected, claimedCharacter: 0);
        Assert.Equal(2, targets.Count);
        Assert.Equal([Server, other], targets.Select(t => t.ServerAddress).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void CharacterStampedEvent_WithNoMatchingStream_RoutesNowhere()
    {
        IReadOnlyList<Ref> connected = [new(Server, Catbank)];

        // RaymondKrah isn't connected here → nothing to send over (dropped locally instead of bounced by the server).
        Assert.Empty(RemoteBusConnectionManager.SelectTargets(connected, RaymondKrah));
    }
}
