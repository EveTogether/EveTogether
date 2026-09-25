using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

internal sealed class DeleteSkillPlanCommandHandler(ISkillPlanRepository repository, IEventBus eventBus)
    : ICommandHandler<DeleteSkillPlanCommand, Result>
{
    public async Task<Result> Handle(DeleteSkillPlanCommand command, CancellationToken cancellationToken = default)
    {
        bool deleted = await repository.DeleteAsync(command.PlanId, cancellationToken);
        if (!deleted)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "This plan no longer exists.", "Skills.Plans"));
        }

        await eventBus.PublishAsync(
            new SkillPlansChangedEvent(command.CharacterId, SkillPlansChangeKind.PlanDeleted, command.PlanId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
