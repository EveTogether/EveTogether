using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>An activity as the activity strip counts it (ET-292): no row, no faces — twelve weeks of history are up to
/// a thousand of these, and only the month in view gets rows built. Its figures come off the overview DTO the way
/// <see cref="ActivityOverviewRowViewModel"/> takes them, so a day counted here and the same day's header add up to
/// the same text. <see cref="TypeId"/> and <see cref="CrewCharacterIds"/> are the two facts the TYPES/CHARACTERS
/// filter needs (ET-293) to hide a day's strip shading the same way it hides the day's own rows — a plain figure
/// record otherwise has no type or crew to filter on at all.</summary>
public sealed record RunsActivityFacts(
    DateTime StartedAtLocal,
    TimeSpan Duration,
    decimal? NetIsk,
    IskBreakdown Isk,
    IReadOnlyList<string> ServerAddresses,
    RunTypeId TypeId,
    IReadOnlyList<long> CrewCharacterIds) : IRunsActivityFigures
{
    public DateOnly Day => DateOnly.FromDateTime(StartedAtLocal);

    /// <param name="facts">The same cache <see cref="ActivityOverviewRowViewModel"/> resolves TYPE through, so a
    /// site the row calls "Homefront" is never counted here under plain "Site" for want of the SDE fallback.</param>
    public static RunsActivityFacts From(ActivityOverviewRowDto row, RunRowFacts facts) => new(
        row.StartedAtUtc.ToLocalTime(),
        TimeSpan.FromSeconds(row.DurationSeconds),
        row.OwnIsk.HasFigure ? row.OwnIsk.Total : null,
        row.OwnIsk,
        [.. row.ServerSyncStates.Select(state => state.ServerAddress).Distinct()],
        facts.TypeOf(row.ActivityKind, row.SignatureGroupSnapshot, row.SiteTypeId, row.SiteName).Id,
        [.. row.Crew.Select(member => member.CharacterId)]);
}
