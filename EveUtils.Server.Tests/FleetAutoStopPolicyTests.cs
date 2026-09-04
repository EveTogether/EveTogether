using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The rule behind the automatic stop (ET-167), on its own: which started fleets go back to standing by, on which of
/// the two grounds, and when the ground may not be believed. The point of the ticket is that the two grounds are not
/// symmetrical — an empty roster is an empty roster at eleven o'clock too, while "everyone has gone quiet" is exactly
/// what a restart manufactures for the whole server at once — so most of what is asserted here is the asymmetry.
/// </summary>
public class FleetAutoStopPolicyTests
{
    private static readonly FleetCleanupOptions Options = FleetCleanupOptions.Default;

    // A fixed clock well clear of 11:00 UTC, so a test that is not about downtime never accidentally is.
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 20, 30, 0, TimeSpan.Zero);

    private static FleetStopTrigger? Evaluate(
        FleetPresenceCensus census,
        DateTimeOffset? lastActivityAt = null,
        bool brakeEngaged = false,
        FleetActivation activation = FleetActivation.Active,
        FleetState state = FleetState.Active,
        DateTimeOffset? now = null) =>
        FleetAutoStopPolicy.Evaluate(
            state, activation, census,
            lastActivityAt ?? (now ?? Now) - TimeSpan.FromHours(2),
            now ?? Now, brakeEngaged, Options);

    private static FleetMember Member(int characterId, DateTimeOffset? lastSeenAt, bool external = false) =>
        new() { CharacterId = characterId, LastSeenAt = lastSeenAt, IsExternal = external };

    // ── Everyone left ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyRoster_StandsTheFleetDown()
    {
        Assert.Equal(FleetStopTrigger.RosterEmpty, Evaluate(new FleetPresenceCensus(0, 0, 0)));
    }

    /// <summary>
    /// The one grace on this ground, and it is not about downtime: between starting a fleet and the first accepted
    /// invite the roster is legitimately empty, and the sweep would otherwise stand the fleet back down with its
    /// invitations still out.
    /// </summary>
    [Fact]
    public void AnEmptyRosterThatOnlyJustEmptied_IsLeftAlone()
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(0, 0, 0), lastActivityAt: Now - TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// The asymmetry, stated: whoever left stayed gone, so this ground does not answer to the brake. Passing an
    /// engaged brake here is the whole test — it must change nothing.
    /// </summary>
    [Fact]
    public void AnEmptyRoster_StandsTheFleetDownEvenDuringDowntime()
    {
        Assert.Equal(
            FleetStopTrigger.RosterEmpty,
            Evaluate(new FleetPresenceCensus(0, 0, 0), brakeEngaged: true));
    }

    // ── Everyone offline ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryMemberGoneQuiet_StandsTheFleetDown()
    {
        Assert.Equal(
            FleetStopTrigger.AllMembersOffline,
            Evaluate(new FleetPresenceCensus(MemberCount: 3, PresentCount: 0, EverHeardCount: 3)));
    }

    /// <summary>The acceptance criterion "a fleet with one member still flying does not stop".</summary>
    [Fact]
    public void OneMemberStillThere_KeepsTheFleetRunning()
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(MemberCount: 3, PresentCount: 1, EverHeardCount: 3)));
    }

    [Fact]
    public void EveryMemberGoneQuiet_IsWithheldWhileTheBrakeIsOn()
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(3, 0, 3), brakeEngaged: true));
    }

    /// <summary>
    /// ET-70's rule, and the reason an FC can start a fleet before their pilots log in: silence that was never
    /// preceded by contact is not evidence. Without this the fleet would stand itself down ninety seconds after
    /// being started.
    /// </summary>
    [Fact]
    public void AFleetNobodyHasEverPublishedInto_IsNotReadAsEmptied()
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(MemberCount: 4, PresentCount: 0, EverHeardCount: 0)));
    }

    // ── Phases this rule may not touch ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FleetActivation.Forming)]
    [InlineData(FleetActivation.Concluded)]
    public void OnlyAStartedFleetCanBeStopped(FleetActivation activation)
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(0, 0, 0), activation: activation));
        Assert.Null(Evaluate(new FleetPresenceCensus(3, 0, 3), activation: activation));
    }

    [Fact]
    public void AnArchivedFleet_IsNobodysBusinessHere()
    {
        Assert.Null(Evaluate(new FleetPresenceCensus(0, 0, 0), state: FleetState.Archived));
    }

    // ── The census ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCensus_CountsSilenceAgainstTheOneSilenceWindowThereIs()
    {
        var members = new List<FleetMember>
        {
            Member(1, Now - FleetMemberPresence.SilentAfter + TimeSpan.FromSeconds(5)), // just inside → present
            Member(2, Now - FleetMemberPresence.SilentAfter - TimeSpan.FromSeconds(5)), // just outside → silent
            Member(3, lastSeenAt: null),                                                // never heard
        };

        var census = FleetPresenceCensus.Take(members, Now);

        Assert.Equal(3, census.MemberCount);
        Assert.Equal(1, census.PresentCount);
        Assert.Equal(2, census.EverHeardCount);
    }

    /// <summary>
    /// An external member is a roster row without a client — permanently unheard by definition. Counting their
    /// silence as evidence would stand a fleet down the moment it was started with one on the roster; counting them
    /// as nobody would let a fleet of externals read as an empty roster. They are members, and they are not evidence.
    /// </summary>
    [Fact]
    public void AnExternalMember_CountsAsAMemberButNeverAsEvidence()
    {
        var census = FleetPresenceCensus.Take([Member(1, lastSeenAt: null, external: true)], Now);

        Assert.Equal(1, census.MemberCount);
        Assert.Equal(0, census.EverHeardCount);
        Assert.Null(Evaluate(census));
    }
}
