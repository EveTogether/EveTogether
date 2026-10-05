using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>
/// Adds rows to a plan from one source (ET-355), the one public write every + SKILL/FROM FIT/FROM ITEM/IMPORT FROM TEXT
/// action and the ET-356/357 hooks go through. The repository dedupes on (skill, level), and a batch that adds nothing
/// publishes no signal.
/// </summary>
public sealed record AddSkillPlanRowsCommand(
    int CharacterId, int PlanId, SkillPlanRowSource Source, string? SourceRef, IReadOnlyList<SkillPlanRowDraft> Rows)
    : ICommand<Result<int>>;
