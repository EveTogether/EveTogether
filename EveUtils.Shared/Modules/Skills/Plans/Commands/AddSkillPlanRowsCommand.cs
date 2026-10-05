using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>
/// Adds rows to a plan from one source (ET-355), the one public write every + SKILL/FROM FIT/FROM ITEM/IMPORT FROM TEXT
/// action and the ET-356/357 hooks go through. The repository dedupes on (skill, level), and a batch that adds nothing
/// publishes no signal. With <paramref name="SourceLabel"/> the source itself is recorded too, even when it adds no
/// row, along with the levels it asked for that were already trained (<paramref name="Dropped"/>).
/// </summary>
public sealed record AddSkillPlanRowsCommand(
    int CharacterId, int PlanId, SkillPlanRowSource Source, string? SourceRef, IReadOnlyList<SkillPlanRowDraft> Rows,
    string? SourceLabel = null, IReadOnlyList<SkillPlanRowDraft>? Dropped = null)
    : ICommand<Result<int>>;
