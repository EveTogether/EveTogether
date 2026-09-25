using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>Deletes a plan and every one of its rows (ET-355).</summary>
public sealed record DeleteSkillPlanCommand(int CharacterId, int PlanId) : ICommand<Result>;
