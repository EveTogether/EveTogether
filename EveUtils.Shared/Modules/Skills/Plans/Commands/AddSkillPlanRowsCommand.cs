using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>
/// Adds rows to a plan from one source (ET-355) — the one public write every + SKILL/FROM FIT/FROM ITEM action, and
/// IMPORT FROM TEXT (<see cref="SkillPlanRowSource.Text"/>), goes through. ET-356 and ET-357's own "+ to plan" hooks
/// call this too rather than building a second write path. The repository dedupes on (skill, level); a batch that
/// adds nothing publishes no signal.
/// </summary>
public sealed record AddSkillPlanRowsCommand(
    int CharacterId, int PlanId, SkillPlanRowSource Source, string? SourceRef, IReadOnlyList<SkillPlanRowDraft> Rows)
    : ICommand<Result<int>>;
