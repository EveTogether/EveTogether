using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// One-time correction for ET-490: a run started by hand from the site list was stored without a system, and nothing
/// stores where a pilot was at a given moment, so the only proof left is the run's own group. A saved run without a
/// system gets the system of the other runs filed under its group code, but only when they all agree on one — the
/// characters of a group fly together, and two different systems prove nothing about this one, so it is left alone.
/// Idempotent: a run that has a system is never touched, and a second pass finds nothing to fill. A filled run
/// that was already published turns Outdated, as for every other correction (ET-215). Returns how many runs were filled.
/// </summary>
public sealed record FillRunSolarSystemsCommand : ICommand<Result<int>>;
