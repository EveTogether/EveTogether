using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class CreateFleetCompositionCommandHandler(IFleetCompositionRepository repository)
    : ICommandHandler<CreateFleetCompositionCommand, Result<long>>
{
    public async Task<Result<long>> Handle(CreateFleetCompositionCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Composition name is required.", "FleetComposition"));

        var now = DateTimeOffset.UtcNow;
        var id = await repository.AddAsync(new FleetComposition
        {
            Name = command.Name.Trim(),
            Description = command.Description,
            OwnerCharacterId = command.ActingCharacterId,
            IsClientOnly = command.IsClientOnly,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);

        return Result<long>.Success(id);
    }
}
