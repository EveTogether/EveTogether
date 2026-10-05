using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

public sealed record RenameSkillPlanCommand(int CharacterId, int PlanId, string Name) : ICommand<Result>;
