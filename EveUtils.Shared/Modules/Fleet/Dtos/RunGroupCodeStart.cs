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
public sealed record RunGroupCodeStart(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime StartedAtUtc,
    bool IsFleetCommander,
    string? SiteName = null,
    string? SolarSystemName = null,
    string? Signature = null);
