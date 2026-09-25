using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

internal sealed class CreateSkillPlanCommandHandler(ISkillPlanRepository repository, IEventBus eventBus)
    : ICommandHandler<CreateSkillPlanCommand, Result<int>>
{
    public async Task<Result<int>> Handle(CreateSkillPlanCommand command, CancellationToken cancellationToken = default)
    {
        var plan = new SkillPlan { CharacterId = command.CharacterId, Name = command.Name, CreatedAtUtc = DateTime.UtcNow };
        int planId = await repository.CreateAsync(plan, cancellationToken);
        await eventBus.PublishAsync(
            new SkillPlansChangedEvent(command.CharacterId, SkillPlansChangeKind.PlanCreated, planId), EventTarget.Local, cancellationToken);
        return Result<int>.Success(planId);
    }
}
