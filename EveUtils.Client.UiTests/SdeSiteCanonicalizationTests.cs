using System.Collections.Generic;
using System.Globalization;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Two counter-proofs for the catalogue-twin picker fix (Raymond, 2026-09-05), on top of ET-125/ET-126's own six.
/// </summary>
public sealed class SdeSiteCanonicalizationTests
{
    /// <summary>Not a twin: <c>Sansha's Command Relay Outpost</c> disagrees on archetype (<c>2251</c> a Combat Site,
    /// <c>2406</c> an Escalation), so both rows survive, unmerged.</summary>
    [Fact]
    public void SitesDifferingOnArchetype_StayTwoRows()
    {
        const string collidingName = "Sansha's Command Relay Outpost";
        IReadOnlyList<SdeSite> canonical = SdeSiteCanonicalization.Canonicalize(
        [
            new SdeSite(2251, collidingName, null, "Combat Site", null, "Sansha's Nation", null, 3, false, []),
            new SdeSite(2406, collidingName, null, "Escalation", null, "Sansha's Nation", null, 3, false, [])
        ]);

        Assert.Equal(2, canonical.Count);
        Assert.Contains(canonical, site => site.DungeonId == 2251);
        Assert.Contains(canonical, site => site.DungeonId == 2406);
    }

    /// <summary>
    /// A genuine twin (Angel Sanctum, identical on every catalogue field, only the id differs) collapses to one row
    /// on the lowest dungeonId — regardless of the order the rows arrive in, and regardless of the thread's culture.
    /// Both are load-bearing: a rule that only happens to agree twice in a row, or only under the invariant culture
    /// this test process usually runs under, proves nothing about two independent clients agreeing on the same id.
    /// A comma-decimal culture is picked deliberately — the field most at risk of an implicit ToString() is a
    /// numeric one (DED rating).
    /// </summary>
    [Fact]
    public void GenuineTwins_CollapseToTheLowestId_RegardlessOfInputOrderOrThreadCulture()
    {
        var lower = new SdeSite(2311, "Angel Sanctum", null, "Combat Site", null, "Angel Cartel", null, 3, false, []);
        var higher = new SdeSite(2333, "Angel Sanctum", null, "Combat Site", null, "Angel Cartel", null, 3, false, []);

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL"); // comma as decimal separator
            SdeSite forward = Assert.Single(SdeSiteCanonicalization.Canonicalize([lower, higher]));
            SdeSite reversed = Assert.Single(SdeSiteCanonicalization.Canonicalize([higher, lower]));

            Assert.Equal(2311, forward.DungeonId);
            Assert.Equal(2311, reversed.DungeonId);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
