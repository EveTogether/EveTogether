using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class EditFleetCompositionEntryCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer,
    CompositionChangeSignal changes) : ICommandHandler<EditFleetCompositionEntryCommand, Result>
{
    public async Task<Result> Handle(EditFleetCompositionEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (CompositionSkillMinimums.Validate(command.SkillMinimums) is { } invalid)
        {
            return Result.Failure(invalid);
        }

        var entry = await repository.GetEntryAsync(command.EntryId, cancellationToken);
        if (entry is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Entry not found.", "FleetComposition"));

        var role = await repository.GetRoleAsync(entry.RoleId, cancellationToken);
        var composition = role is null ? null : await repository.GetAsync(role.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        entry.EntryMinCount = command.EntryMinCount;
        if (command.SkillMinimums is not null)
        {
            entry.SkillMinimums = CompositionSkillMinimums.ToEntities(command.SkillMinimums);
        }

        await repository.UpdateEntryAsync(entry, cancellationToken);
        await changes.PublishAsync(composition.Id, CompositionChangeKind.Edited, cancellationToken);
        return Result.Success();
    }
}
