using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// Turns what a run already carries into a <see cref="RunTypeId"/> — never a fourth source, never a guess from a
/// site name (ET-226 pitfall: "Combat Site"/"Data Site" is a scanner fact, a name like "Production Installation" is
/// only a pattern). <see cref="ActivityKind"/> settles Mission and Abyssal outright; a site needs a second fact —
/// either the scanner's own group text, recorded once at copy time (<see cref="Entities.Run.SignatureGroupSnapshot"/>)
/// and never re-derived afterwards (the same reasoning as <c>FitNameSnapshot</c> and <c>CharacterNameSnapshot</c>,
/// ET-212), or the dungeon id a single catalogue match resolved to (<see cref="Entities.Run.SiteTypeId"/>, ET-228).
///
/// Mining without a site (ET-229) is not resolved here yet: it needs a source this ticket does not add (a belt with
/// no site at all). Its <see cref="RunTypeId"/> member exists so that ticket adds one arm, not a second resolver.
///
/// Homefront (ET-228) is resolved from <paramref name="siteTypeId"/> below rather than the scanner's own group
/// text: that text is never caught as "Homefront Operations …" in practice (ET-226 measured it, ET-177's own vague
/// guess), and even when it were, a raw text pattern is exactly the guess this ticket exists to stop taking. Read
/// unconditionally for <see cref="ActivityKind.Site"/> — a mission's own id space (<see cref="SiteTypeSource.Mission"/>)
/// never reaches this arm at all, since <see cref="ActivityKind.Mission"/> resolves above it, and the one other
/// source, <see cref="SiteTypeSource.Uncatalogued"/>, only ever pairs with a 0 here, which is never a homefront id.
/// </summary>
public static class RunTypeResolver
{
    public static RunTypeId Resolve(ActivityKind activityKind, string? signatureGroupSnapshot, int siteTypeId = 0) =>
        activityKind switch
        {
            ActivityKind.Mission => RunTypeId.Mission,
            ActivityKind.Abyssal => RunTypeId.Abyssal,
            ActivityKind.Site => _ResolveSite(signatureGroupSnapshot, siteTypeId),
            _ => RunTypeId.Unknown
        };

    // The dungeon id is the more specific fact where it is known (ET-228): archetype 70's 24 ids are the one
    // reliable way to tell a homefront from an ordinary site of the same scanner group.
    private static RunTypeId _ResolveSite(string? signatureGroup, int siteTypeId) =>
        HomefrontCatalogue.IsHomefrontDungeonId(siteTypeId) ? RunTypeId.Homefront : _ResolveSiteGroup(signatureGroup);

    // Exact match on the scanner's own English group text (ET-79: the group column is whatever language the EVE
    // client runs in, so a localised client's group text falls through to Unknown here rather than a wrong kind —
    // the same honesty gap the raw SignatureGroup already had before this ticket).
    private static RunTypeId _ResolveSiteGroup(string? signatureGroup) => signatureGroup switch
    {
        "Combat Site" => RunTypeId.CombatSite,
        "Data Site" => RunTypeId.DataSite,
        "Relic Site" => RunTypeId.RelicSite,
        "Gas Site" => RunTypeId.GasSite,
        "Ore Site" => RunTypeId.OreSite,
        "Wormhole" => RunTypeId.Wormhole,
        _ => RunTypeId.Unknown
    };
}
