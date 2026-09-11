using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// Turns what a run already carries into a <see cref="RunTypeId"/> — never a fourth source, never a guess from a
/// site name (ET-226 pitfall: "Combat Site"/"Data Site" is a scanner fact, a name like "Production Installation" is
/// only a pattern). <see cref="ActivityKind"/> settles Mission and Abyssal outright; only <see cref="ActivityKind.Site"/>
/// needs a second fact, and that fact is the scanner's own group text, recorded once at copy time
/// (<see cref="Entities.Run.SignatureGroupSnapshot"/>) and never re-derived afterwards — the same reasoning as
/// <c>FitNameSnapshot</c> and <c>CharacterNameSnapshot</c> (ET-212).
///
/// Homefront (ET-228) and mining without a site (ET-229) are not resolved here yet: both need a source this ticket
/// does not add (the site's archetype, and a belt with no site at all). Their <see cref="RunTypeId"/> members exist
/// so those tickets add one arm each, not a second resolver.
/// </summary>
public static class RunTypeResolver
{
    public static RunTypeId Resolve(ActivityKind activityKind, string? signatureGroupSnapshot) => activityKind switch
    {
        ActivityKind.Mission => RunTypeId.Mission,
        ActivityKind.Abyssal => RunTypeId.Abyssal,
        ActivityKind.Site => _ResolveSiteGroup(signatureGroupSnapshot),
        _ => RunTypeId.Unknown
    };

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
