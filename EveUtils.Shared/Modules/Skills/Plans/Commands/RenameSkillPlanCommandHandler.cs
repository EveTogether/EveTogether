using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

internal sealed class RenameSkillPlanCommandHandler(ISkillPlanRepository repository, IEventBus eventBus)
    : ICommandHandler<RenameSkillPlanCommand, Result>
{
    public async Task<Result> Handle(RenameSkillPlanCommand command, CancellationToken cancellationToken = default)
    {
        bool renamed = await repository.RenameAsync(command.PlanId, command.Name, cancellationToken);
        if (!renamed)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "This plan no longer exists.", "Skills.Plans"));
        }

        await eventBus.PublishAsync(
            new SkillPlansChangedEvent(command.CharacterId, SkillPlansChangeKind.PlanRenamed, command.PlanId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
