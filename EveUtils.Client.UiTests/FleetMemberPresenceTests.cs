using System;
using System.Linq;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-70, the parts with no screen attached: the verdict, the badge's third bucket, and what a client publishes about
/// itself. A fleet can hold members who are in it and not online, and until now one of those looked exactly like a
/// member we simply had no data for — a difference an FC steers on.
/// </summary>
public class FleetMemberPresenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);

    // ---- the verdict --------------------------------------------------------------------------------------------

    /// <summary>ET-71's rule, unmoved: for our own pilot the local sweep is the answer, whatever the wire says.</summary>
    [Theory]
    [InlineData(true, FleetMemberPresenceState.Online)]
    [InlineData(false, FleetMemberPresenceState.Offline)]
    public void OurOwnPilot_IsReadFromTheLocalSweep(bool inGame, FleetMemberPresenceState expected) =>
        Assert.Equal(expected, FleetMemberPresence.Read(inGame, PresenceState.Unknown, isSilent: false));

    /// <summary>The easy half of the ticket: their client is running, and it says the game is not.</summary>
    [Theory]
    [InlineData(PresenceState.InGame, FleetMemberPresenceState.Online)]
    [InlineData(PresenceState.NotInGame, FleetMemberPresenceState.Offline)]
    public void AFleetMate_IsReadFromWhatTheirOwnClientReports(PresenceState reported, FleetMemberPresenceState expected) =>
        Assert.Equal(expected, FleetMemberPresence.Read(inGameLocally: null, reported, isSilent: false));

    /// <summary>
    /// The hard half: a client that has been shut down never sends the message saying so, so the evidence is that
    /// nothing arrives any more.
    /// </summary>
    [Fact]
    public void AFleetMateWhoWasHeardFromAndHasGoneQuiet_IsOffline() =>
        Assert.Equal(
            FleetMemberPresenceState.Offline,
            FleetMemberPresence.Read(inGameLocally: null, PresenceState.InGame, isSilent: true));

    /// <summary>
    /// The distinction the whole design rests on. A pilot who has never been heard from is <b>unknown</b>, not
    /// offline: they may simply share nothing, and every metric can be switched off. Reading their silence as
    /// departure would call an entire category of member "gone" on no evidence at all — and it is the assumption
    /// that would have broken quietly, because it is right for everyone who ever publishes.
    /// </summary>
    [Fact]
    public void AFleetMateNothingWasEverHeardFrom_IsUnknown_NotOffline()
    {
        var verdict = FleetMemberPresence.Read(inGameLocally: null, PresenceState.Unknown, isSilent: false);

        Assert.Equal(FleetMemberPresenceState.Unknown, verdict);
        Assert.NotEqual(FleetMemberPresenceState.Offline, verdict);
    }

    /// <summary>…and the same thing measured on the clock: no contact at all is not silence.</summary>
    [Fact]
    public void NeverHeardFrom_IsNotSilence() =>
        Assert.False(FleetMemberPresence.IsSilent(lastHeardAt: null, Now));

    /// <summary>
    /// The boundary, stated rather than assumed. Ninety seconds is not "about a minute and a half": at exactly the
    /// window the pilot is still here, and one tick past it they are not. Both sides are asserted because a
    /// threshold tested only in the middle would pass with the comparison inverted.
    /// </summary>
    [Fact]
    public void TheSilenceWindow_IsExclusive_AtItsBoundary()
    {
        var heard = Now - FleetMemberPresence.SilentAfter;

        Assert.False(FleetMemberPresence.IsSilent(heard, Now));
        Assert.True(FleetMemberPresence.IsSilent(heard - TimeSpan.FromMilliseconds(1), Now));
        Assert.False(FleetMemberPresence.IsSilent(heard + TimeSpan.FromMilliseconds(1), Now));
    }

    /// <summary>
    /// The threshold has to stay clear of how long the transport itself is allowed to say nothing while everything
    /// is fine. <c>ServerConnection</c> gives a half-open stream 45 s before it notices, then spends up to another
    /// 5 s connecting; a window inside that would blink a pilot who is flying off the screen on one hiccup, which is
    /// the one thing the ticket says must not happen. Pinned so a later tuning pass has to face the reason.
    /// </summary>
    [Fact]
    public void TheSilenceWindow_ClearsTheTransportsOwnWorstCaseSilence()
    {
        var transportWorstCase = TimeSpan.FromSeconds(45) + TimeSpan.FromSeconds(5);

        Assert.True(FleetMemberPresence.SilentAfter > transportWorstCase);
        // …and the stored timestamp is refreshed well inside the window, so a throttled write can never on its own
        // make an actively-publishing member look silent.
        Assert.True(FleetMemberPresence.SeenWriteThrottle < FleetMemberPresence.SilentAfter);
    }

    // ---- the badge's third bucket -------------------------------------------------------------------------------

    /// <summary>
    /// Offline is counted apart from "no position fix" (ET-70's advice, taken). Both stay out of the ratio — the
    /// ET-63 rule — but an FC reading "three are gone" acts differently from one reading "three share no location".
    /// </summary>
    [Fact]
    public void TheBadge_CountsOffline_ApartFromMembersWithNoLocationFix()
    {
        FleetMemberStanding[] members =
        [
            .. FleetStandings.At("Jita", "Jita", "Perimeter", null),
            FleetStandings.Gone,
            FleetStandings.Gone,
        ];

        var presence = FleetCommanderPresence.From("Jita", members);

        Assert.Equal(2, presence.InSystem);
        Assert.Equal(3, presence.Known);
        Assert.Equal(1, presence.UnknownLocations);   // the one who shares nothing, and only them
        Assert.Equal(2, presence.Offline);
        Assert.Equal(6, presence.Total);
        Assert.Equal("◉ 2/3 WITH FC (2 offline, 1 unknown)", presence.BadgeText);
        Assert.Contains("2 more are offline", presence.Tooltip);
        Assert.Contains("1 more shares no location", presence.Tooltip);
    }

    /// <summary>One suffix at a time when there is only one thing to say, and none at all on the plain case.</summary>
    [Theory]
    [InlineData(0, 0, "◉ 2/2 WITH FC")]
    [InlineData(1, 0, "◉ 2/2 WITH FC (1 offline)")]
    [InlineData(0, 1, "◉ 2/2 WITH FC (1 unknown)")]
    public void TheBadgeSuffix_NamesOnlyWhatThereIsToSay(int offline, int unknown, string expected)
    {
        FleetMemberStanding[] members =
        [
            .. FleetStandings.At("Jita", "Jita"),
            .. Enumerable.Repeat(FleetStandings.Gone, offline),
            .. FleetStandings.At(Enumerable.Repeat((string?)null, unknown).ToArray()),
        ];

        Assert.Equal(expected, FleetCommanderPresence.From("Jita", members).BadgeText);
    }

    /// <summary>
    /// With nobody's location known the ratio reads "—", which already says there is nothing to count. Repeating it
    /// as "(n unknown)" would be noise; "(n offline)" is the news that dash does not carry, and often the reason
    /// for it.
    /// </summary>
    [Fact]
    public void WithNoLocationKnownAtAll_TheBadgeStillNamesTheMembersWhoAreGone()
    {
        FleetMemberStanding[] members = [.. FleetStandings.At(null, null), FleetStandings.Gone];

        var presence = FleetCommanderPresence.From("Jita", members);

        Assert.True(presence.IsUnknown);
        Assert.Equal("◉ — WITH FC (1 offline)", presence.BadgeText);
    }

    // ---- what a client publishes about itself -------------------------------------------------------------------

    [Theory]
    [InlineData(true, PresenceState.InGame)]
    [InlineData(false, PresenceState.NotInGame)]
    public void ThePresenceSource_PublishesWhatTheLocalSweepSays(bool inGame, PresenceState expected)
    {
        var sample = Assert.Single(new PresenceMetricSource(new StubPresence(inGame)).Sample(100, 42, 1));

        Assert.Equal(MetricKind.Presence, sample.Kind);
        Assert.Equal((double)expected, sample.Value);
    }

    /// <summary>
    /// It publishes even with no verdict, and that is the point rather than a gap. The sample's arrival is itself
    /// the statement "my EVE Together is running" — it is what keeps the member's <c>LastSeenAt</c> fresh, and so
    /// what makes the silence after a closed client mean anything. A source that fell quiet while the registry was
    /// still loading would report the pilot as having left.
    /// </summary>
    [Fact]
    public void ThePresenceSource_StillPublishes_WhenItHasNoVerdictToGive()
    {
        var sample = Assert.Single(new PresenceMetricSource(new StubPresence(null)).Sample(100, 42, 1));

        Assert.Equal((double)PresenceState.Unknown, sample.Value);
    }

    /// <summary>Nor does a missing presence service silence it — same reason, and it must claim nothing either.</summary>
    [Fact]
    public void ThePresenceSource_StillPublishes_WithNoPresenceServiceAtAll()
    {
        var sample = Assert.Single(new PresenceMetricSource().Sample(100, 42, 1));

        Assert.Equal((double)PresenceState.Unknown, sample.Value);
    }

    private sealed class StubPresence(bool? inGame) : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => inGame;
        public bool? IsInGame(int characterId) => inGame;
        public IDisposable Subscribe(Action handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
