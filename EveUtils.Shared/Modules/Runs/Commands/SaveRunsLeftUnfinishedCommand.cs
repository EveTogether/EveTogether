using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Commit every run that has been stopped for a day without anyone finishing it, as it stands. <c>Stopped</c> is not
/// a resting place (Raymond, 2026-09-04): a run left there is work that was flown, and throwing it away is a choice
/// only the pilot may make — so time decides in favour of keeping it, never of losing it.
///
/// Judged when it is asked, at startup and when the runs screen loads, rather than by a timer that keeps running:
/// the deadline is a property of the row's own stop stamp, so a client that was off for a week catches up in one go.
/// </summary>
/// <param name="NowUtc">The moment the deadline is measured against — passed in rather than read here, so the caller
/// and the runs it saves share one clock.</param>
public sealed record SaveRunsLeftUnfinishedCommand(DateTime NowUtc) : ICommand<Result<int>>;
