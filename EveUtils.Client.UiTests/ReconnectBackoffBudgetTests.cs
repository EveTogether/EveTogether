using System;
using System.Linq;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The reconnect backoff and the fleet's offline threshold are one arithmetic, in two files. A character publishes
/// nothing for a whole reconnect cycle — ReceiveDeadline noticing the drop, the backoff step, then ConnectTimeout —
/// so every other client reads them as silent for exactly that long. If the worst cycle outgrows
/// <see cref="FleetMemberPresence.SilentAfter"/>, a pilot who is flying perfectly well drops off every fleet screen
/// and nothing in either file says why (ET-70, ET-95).
///
/// This held by accident until ET-95: the backoff could never grow past its second step, so its 60 s tail was
/// unreachable. Whoever raises the cap or lowers the threshold next gets this test red instead of a phantom-offline
/// pilot in a live fleet.
/// </summary>
public class ReconnectBackoffBudgetTests
{
    private static TimeSpan WorstReconnectCycle =>
        ServerConnection.ReceiveDeadline
        + TimeSpan.FromSeconds(ServerConnection.BackoffSeconds.Max())
        + ServerConnection.ConnectTimeout;

    [Fact]
    public void TheWorstReconnectCycle_StaysInsideTheOfflineThreshold()
    {
        Assert.True(WorstReconnectCycle < FleetMemberPresence.SilentAfter,
            $"A reconnect can silence a character for {WorstReconnectCycle.TotalSeconds:0}s, but a fleet calls them "
            + $"offline after {FleetMemberPresence.SilentAfter.TotalSeconds:0}s. Re-derive one of the two.");
    }

    [Fact]
    public void TheBackoffStartsImmediateAndOnlyGrows()
    {
        // The first drop must be retried at once — a healthy connection that hiccups may not pay a wait — and the
        // steps after it must climb, or the table is not a backoff at all.
        Assert.Equal(0, ServerConnection.BackoffSeconds[0]);
        Assert.Equal(
            ServerConnection.BackoffSeconds.OrderBy(s => s).ToArray(),
            ServerConnection.BackoffSeconds);
        Assert.Equal(ServerConnection.BackoffSeconds.Distinct().Count(), ServerConnection.BackoffSeconds.Length);
    }
}
