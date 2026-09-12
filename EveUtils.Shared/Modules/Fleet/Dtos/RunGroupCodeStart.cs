using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The commander started a shared run, announced to the fleet. <see cref="SiteName"/> and
/// <see cref="SolarSystemName"/> are what the receiving member is shown: the group code is keyed on fleet +
/// activity kind and knows no site, so this reaches members flying somewhere else entirely, and the site is the
/// only thing that lets such a pilot see it is not their run. Both are nullable — an older client, or a start
/// before the location is known, names neither.
/// </summary>
/// <param name="Signature">The commander's scan id for the site, e.g. RUS-326. Safe to show a member because a
/// cosmic signature's id is a property of the system and not of the pilot: every capsuleer in that system reads the
/// same code off their own scanner, which is the whole reason a corp's mapping tools can share signature ids at all.
/// It is re-rolled at downtime, and the run does not outlive one (ET-151).</param>
/// <param name="SignatureGroupSnapshot">The scanner's own group text behind <see cref="SiteName"/> (ET-226), so a
/// member's own run resolves to the same TYPE the commander's does instead of the honest-but-poorer "Site" a member
/// with no scan of their own would otherwise show (ET-239). Null on an older commander's client, which a member on
/// this build reads the same as a manual start with no scan of its own.</param>
/// <param name="SiteTypeId">The dungeon id the commander's own copy resolved to (ET-228), same reasoning and same
/// gap as <see cref="SignatureGroupSnapshot"/> above (ET-268): a member joining this run never copied the signature
/// themselves, so they have no <c>MatchedSites</c> of their own to resolve a homefront from, and read "Site" for a
/// run the commander's own window already knew was Homefront. 0 on an older commander's client, same as an unmatched
/// site already reads.</param>
/// <param name="AbyssalTierIndex">The pocket's own tier, if the commander's window already knew it at START (ET-241)
/// — never stored by this message, only announced, so a member's window shows the commander's real answer instead of
/// falling back to whatever an unrelated abyssal last left in this member's own remembered settings (the ET-208
/// decision 3 trap: a first-looking value that reads as if it were established).</param>
/// <param name="AbyssalWeatherName">The pocket's own weather, by its plain name (e.g. "Dark") — alongside
/// <see cref="AbyssalTierIndex"/>, same reasoning.</param>
public sealed record RunGroupCodeStart(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime StartedAtUtc,
    bool IsFleetCommander,
    string? SiteName = null,
    string? SolarSystemName = null,
    string? Signature = null,
    string? SignatureGroupSnapshot = null,
    int? AbyssalTierIndex = null,
    string? AbyssalWeatherName = null,
    int SiteTypeId = 0);
