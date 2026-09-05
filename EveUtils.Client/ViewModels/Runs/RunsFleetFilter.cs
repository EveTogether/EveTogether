using System;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Scopes the runs screen to one fleet's own activity (ET-185, opened from RUNS on a finished fleet row).
/// <see cref="FleetCreatedAtUtc"/> travels along because telling a true zero from an unknowable one needs it
/// (<c>GetFleetRunCoverageQuery</c>) — the screen that filters by fleet is also the one that has to say which of the
/// two an empty result is.</summary>
public sealed record RunsFleetFilter(long FleetId, string FleetName, DateTime FleetCreatedAtUtc);
