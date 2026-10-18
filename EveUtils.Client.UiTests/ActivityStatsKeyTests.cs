using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-513: which runs the earnings statistic counts as the same activity.</summary>
public sealed class ActivityStatsKeyTests
{
    private static readonly SdeSite LowerTwin = new(2311, "Angel Sanctum", null, "Combat Site", null, "Angel Cartel", null, 3, false, []);
    private static readonly SdeSite HigherTwin = new(2333, "Angel Sanctum", null, "Combat Site", null, "Angel Cartel", null, 3, false, []);
    private static readonly SdeSite RelayCombat = new(2251, "Sansha's Command Relay Outpost", null, "Combat Site", null, "Sansha's Nation", null, 3, false, []);
    private static readonly SdeSite RelayEscalation = new(2406, "Sansha's Command Relay Outpost", null, "Escalation", null, "Sansha's Nation", null, 3, false, []);

    /// <summary>Both halves of a twin pair, and a run started from a copied name that matched both (saved with id 0,
    /// ET-228), land on the lowest id. Two sites that only share a name stay apart, and the ambiguous name keys on
    /// itself rather than on a guess.</summary>
    [Theory]
    [InlineData(2311, "Angel Sanctum", "site:Site:2311")]
    [InlineData(2333, "Angel Sanctum", "site:Site:2311")]
    [InlineData(0, "Angel Sanctum", "site:Site:2311")]
    [InlineData(2333, null, "site:Site:2333")]
    [InlineData(2406, "Sansha's Command Relay Outpost", "site:Site:2406")]
    [InlineData(0, "Sansha's Command Relay Outpost", "site:Site:name:sansha's command relay outpost")]
    public void For_CatalogueSite_KeysOnTheCanonicalTwin(int siteTypeId, string? siteName, string code)
    {
        Run run = new() { ActivityKind = ActivityKind.Site, SiteTypeSource = SiteTypeSource.Site, SiteTypeId = siteTypeId, SiteName = siteName };

        Assert.Equal(code, ActivityStatsKeys.For(run, [], _Catalogue)?.Code);
    }

    [Theory]
    [InlineData("Data Site")]
    [InlineData("  data   SITE ")]
    [InlineData("DATA SITE")]
    public void For_UncataloguedSite_GroupsOnTheTrimmedNameWhateverTheCase(string siteName)
    {
        Run run = new() { ActivityKind = ActivityKind.Site, SiteTypeSource = SiteTypeSource.Uncatalogued, SiteName = siteName };

        Assert.Equal(ActivityStatsKey.SiteNamed(SiteTypeSource.Uncatalogued, "data site"), ActivityStatsKeys.For(run, [], _Catalogue));
    }

    [Theory]
    [InlineData(ActivityKind.Mission, SiteTypeSource.Mission, 3051, null, 4, "", "mission:3051:4")]
    [InlineData(ActivityKind.Mission, SiteTypeSource.Mission, 0, " The Blockade ", 4, "", "mission:name:the blockade:4")]
    [InlineData(ActivityKind.Abyssal, SiteTypeSource.Site, 0, null, null, "3|Dark", "abyssal:3:Dark")]
    [InlineData(ActivityKind.Abyssal, SiteTypeSource.Site, 0, null, null, "", null)]
    [InlineData(ActivityKind.Site, SiteTypeSource.Site, 10347, "Raid", null, "", "homefront:10347")]
    [InlineData(ActivityKind.Site, SiteTypeSource.Site, 0, null, null, "", null)]
    [InlineData(ActivityKind.Mining, SiteTypeSource.Site, 0, "Belt", null, "", null)]
    [InlineData(ActivityKind.Free, SiteTypeSource.Site, 0, "Roam", null, "", null)]
    public void For_EachActivityKind_KeysOnItsOwnIdentity_AndMiningAndFreerunsHaveNone(ActivityKind kind,
        SiteTypeSource source, int siteTypeId, string? siteName, int? missionLevel, string abyssalFilament, string? code)
    {
        Run run = new() { ActivityKind = kind, SiteTypeSource = source, SiteTypeId = siteTypeId, SiteName = siteName, MissionLevel = missionLevel };
        RunParameter pocket = new() { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = abyssalFilament };

        Assert.Equal(code, ActivityStatsKeys.For(run, [pocket], _Catalogue)?.Code);
    }

    private static IReadOnlyList<SdeSite> _Catalogue(string name) =>
        [.. new[] { LowerTwin, HigherTwin, RelayCombat, RelayEscalation }.Where(site => site.Name == name)];
}
