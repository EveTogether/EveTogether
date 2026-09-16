using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>An activity as the activity strip counts it (ET-292): no row, no type, no faces — twelve weeks of history
/// are up to a thousand of these, and only the month in view gets rows built. Its figures come off the overview DTO the
/// way <see cref="ActivityOverviewRowViewModel"/> takes them, so a day counted here and the same day's header add up to
/// the same text.</summary>
public sealed record RunsActivityFacts(
    DateTime StartedAtLocal,
    TimeSpan Duration,
    decimal? NetIsk,
    IskBreakdown Isk,
    IReadOnlyList<string> ServerAddresses) : IRunsActivityFigures
{
    public DateOnly Day => DateOnly.FromDateTime(StartedAtLocal);

    public static RunsActivityFacts From(ActivityOverviewRowDto row) => new(
        row.StartedAtUtc.ToLocalTime(),
        TimeSpan.FromSeconds(row.DurationSeconds),
        row.OwnIsk.HasFigure ? row.OwnIsk.Total : null,
        row.OwnIsk,
        [.. row.ServerSyncStates.Select(state => state.ServerAddress).Distinct()]);
}
