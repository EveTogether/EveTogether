using System;
using EveUtils.Shared.Modules.Fleet;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// of the ESI fleet-coupling: the live-vs-doctrine roster diff. Pure partition of character ids into who
/// joined (present), who is still missing, and who is in-game but not planned (external).
/// </summary>
public class FleetRosterDiffTests
{
    [Fact]
    public void Diff_PartitionsPresentMissingExternal()
    {
        var diff = FleetRosterDiffer.Diff(plannedCharacterIds: new[] { 100, 200, 300 }, liveCharacterIds: new[] { 100, 200, 999 });

        Assert.Equal(new[] { 100, 200 }, diff.Present);
        Assert.Equal(new[] { 300 }, diff.Missing);
        Assert.Equal(new[] { 999 }, diff.External);
    }

    [Fact]
    public void Diff_EmptyLive_AllPlannedAreMissing()
    {
        var diff = FleetRosterDiffer.Diff(new[] { 1, 2 }, Array.Empty<int>());

        Assert.Equal(new[] { 1, 2 }, diff.Missing);
        Assert.Empty(diff.Present);
        Assert.Empty(diff.External);
    }

    [Fact]
    public void Diff_DedupsAndIsOrderInsensitive()
    {
        var diff = FleetRosterDiffer.Diff(new[] { 2, 1, 1 }, new[] { 1, 1 });

        Assert.Equal(new[] { 1 }, diff.Present);
        Assert.Equal(new[] { 2 }, diff.Missing);
        Assert.Empty(diff.External);
    }
}
