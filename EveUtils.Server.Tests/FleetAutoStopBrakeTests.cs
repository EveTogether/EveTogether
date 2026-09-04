using System;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// When "everybody has gone quiet" may not be believed (ET-167). Jithran's objection was concrete: *"als de server om
/// 11 uur UTC opnieuw opstart dan is iedereen even offline (of bij een andere ongeplande server restart) dan is het
/// niet handig als de fleet gedeactiveerd wordt."* — so this is the daily window, unplanned unavailability, and the
/// tail of both, each against the reconnect grace the cleanup service had already measured for the same reason.
/// </summary>
public class FleetAutoStopBrakeTests
{
    private static readonly TimeSpan Grace = FleetCleanupOptions.Default.ReconnectGrace;

    private static DateTimeOffset At(int hour, int minute, int second = 0) =>
        new(2026, 9, 4, hour, minute, second, TimeSpan.Zero);

    private static bool Engaged(DateTimeOffset now, bool esiUsable = true, DateTimeOffset? lastUnavailable = null) =>
        FleetAutoStopBrake.IsEngaged(now, esiUsable, lastUnavailable, Grace);

    /// <summary>The acceptance criterion "simulate the daily 11:00 UTC window and show that nothing stops".</summary>
    [Theory]
    [InlineData(10, 59, false)] // before: nothing is wrong yet
    [InlineData(11, 0, true)]   // the window opens
    [InlineData(11, 2, true)]   // still inside it
    [InlineData(11, 3, true)]   // window closed, but the pilots are still coming back
    [InlineData(11, 4, true)]   // …still inside the grace
    [InlineData(11, 5, false)]  // grace spent; silence from here is the pilots talking, not the downtime
    [InlineData(12, 0, false)]
    public void TheDailyWindow_IsHeldOpenForTheReconnectGrace(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, Engaged(At(hour, minute)));
    }

    /// <summary>The window it holds open is the gate's own — one definition, widened, not a second one.</summary>
    [Fact]
    public void TheWindowItHoldsOpen_IsTheGatesOwnWindow()
    {
        Assert.True(EsiDowntime.IsScheduledWindow(At(11, 1)));
        Assert.False(EsiDowntime.IsScheduledWindow(At(11, 3)));
        Assert.True(EsiDowntime.IsWithinScheduledWindow(At(11, 3), Grace));
    }

    /// <summary>A stamp carrying an offset is still a moment in UTC; the window is defined there and nowhere else.</summary>
    [Fact]
    public void TheWindow_IsReadInUtc_WhateverOffsetTheStampCarries()
    {
        // 13:01 in a +02:00 zone is 11:01 UTC — inside the window, however the caller happened to stamp it.
        Assert.True(EsiDowntime.IsScheduledWindow(new DateTimeOffset(2026, 9, 4, 13, 1, 0, TimeSpan.FromHours(2))));
        Assert.False(EsiDowntime.IsScheduledWindow(new DateTimeOffset(2026, 9, 4, 11, 1, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void UnplannedUnavailability_EngagesTheBrakeWithoutTheCalendar()
    {
        Assert.True(Engaged(At(20, 0), esiUsable: false));
        Assert.False(Engaged(At(20, 0)));
    }

    /// <summary>
    /// A pass runs every five minutes, so it would otherwise only ever meet the recovered state and release the
    /// brake at precisely the moment clients are queueing to reconnect. Remembering the last pass that saw the gate
    /// closed is what makes recovery a ramp rather than a cliff.
    /// </summary>
    [Fact]
    public void RecoveryIsNotInstant_TheGraceRunsFromTheLastPassThatSawItDown()
    {
        var down = At(20, 0);

        Assert.True(Engaged(down + TimeSpan.FromMinutes(1), lastUnavailable: down));
        Assert.False(Engaged(down + Grace, lastUnavailable: down));
    }

    [Fact]
    public void WithNothingWrongAndNothingRemembered_TheBrakeIsOff()
    {
        Assert.False(Engaged(At(20, 0), esiUsable: true, lastUnavailable: null));
    }
}
