using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

internal sealed class RemoveSkillPlanRowCommandHandler(ISkillPlanRepository repository, IEventBus eventBus)
    : ICommandHandler<RemoveSkillPlanRowCommand, Result>
{
    public async Task<Result> Handle(RemoveSkillPlanRowCommand command, CancellationToken cancellationToken = default)
    {
        bool removed = await repository.RemoveRowAsync(command.CharacterId, command.PlanId, command.SkillTypeId, command.Level, cancellationToken);
        if (!removed)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "This row no longer exists.", "Skills.Plans"));
        }

        await eventBus.PublishAsync(
            new SkillPlansChangedEvent(command.CharacterId, SkillPlansChangeKind.RowRemoved, command.PlanId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
