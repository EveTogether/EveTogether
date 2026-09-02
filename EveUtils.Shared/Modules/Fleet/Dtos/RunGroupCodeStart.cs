using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The commander started a shared run, announced to the fleet. <see cref="SiteName"/> and
/// <see cref="SolarSystemName"/> are what the receiving member is shown: the group code is keyed on fleet +
/// activity kind and knows no site, so this reaches members flying somewhere else entirely, and the site is the
/// only thing that lets such a pilot see it is not their run. Both are nullable — an older client, or a start
/// before the location is known, names neither.
/// </summary>
public sealed record RunGroupCodeStart(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime StartedAtUtc,
    bool IsFleetCommander,
    string? SiteName = null,
    string? SolarSystemName = null);
