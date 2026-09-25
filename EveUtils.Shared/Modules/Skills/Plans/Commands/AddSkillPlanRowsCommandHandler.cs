using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using EveUtils.Shared.Modules.Skills.Plans.Repositories;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

internal sealed class AddSkillPlanRowsCommandHandler(ISkillPlanRepository repository, IEventBus eventBus)
    : ICommandHandler<AddSkillPlanRowsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(AddSkillPlanRowsCommand command, CancellationToken cancellationToken = default)
    {
        var rows = command.Rows.Select(draft => new SkillPlanRow
        {
            SkillTypeId = draft.SkillTypeId,
            Level = draft.Level,
            Source = command.Source,
            SourceRef = command.SourceRef,
            SourceLabel = draft.SourceLabel
        }).ToList();

        int added = await repository.AddRowsAsync(command.CharacterId, command.PlanId, rows, cancellationToken);
        if (added == 0)
        {
            return Result<int>.Success(0); // every row already in the plan — a no-op write publishes no signal
        }

        await eventBus.PublishAsync(
            new SkillPlansChangedEvent(command.CharacterId, SkillPlansChangeKind.RowsAdded, command.PlanId), EventTarget.Local, cancellationToken);
        return Result<int>.Success(added);
    }
}
