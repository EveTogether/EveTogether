using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The activity built from this key today, found straight off <c>ActivitySummary</c>'s own unique indexes
/// on <c>GroupCode</c>/<c>RunId</c> — not through the runs overview, which is now bounded to whichever month is on
/// screen (ET-233) and would miss an activity outside it. Used to follow an activity whose summary id may have
/// changed (a full delete removes the row; a restore builds a new one) back to what it is now, by what it actually
/// is rather than where it happens to sit in a list.</summary>
public sealed record FindActivitySummaryIdQuery(string? GroupCode, Guid? RunId) : IQuery<Result<Guid?>>;
