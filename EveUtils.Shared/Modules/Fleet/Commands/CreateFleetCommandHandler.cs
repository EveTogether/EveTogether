using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class CreateFleetCommandHandler(IFleetRepository repository)
    : ICommandHandler<CreateFleetCommand, Result<long>>
{
    public async Task<Result<long>> Handle(CreateFleetCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet name is required.", "Fleet"));

        var now = DateTimeOffset.UtcNow;
        var id = await repository.AddAsync(new FleetEntity
        {
            Name = command.Name.Trim(),
            Description = command.Description,
            Visibility = command.Visibility,
            FromTime = command.FromTime,
            ToTime = command.ToTime,
            OfflineBehavior = command.OfflineBehavior,
            CreatorCharacterId = command.ActingCharacterId,
            State = FleetState.Active,
            CreatedAt = now,
            LastActivityAt = now
        }, cancellationToken);

        // The creator is a default-accepted member at the top of the roster as Fleet Commander:
        // the tree shows one FC, and EnterFleet's membership gate then covers the creator like any other member.
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = id,
            CharacterId = command.ActingCharacterId,
            Role = FleetRole.FleetCommander,
            WingId = -1,
            SquadId = -1,
            JoinTime = now
        }, cancellationToken);

        // EVE parity: a brand-new fleet ships with one default wing holding one default squad, so joiners
        // have a squad to land in and the roster tree is never empty. The structure limits leave room for more.
        var wingId = await repository.AddWingAsync(new FleetWing { FleetId = id, Name = "Wing 1" }, cancellationToken);
        await repository.AddSquadAsync(new FleetSquad { WingId = wingId, Name = "Squad 1" }, cancellationToken);

        return Result<long>.Success(id);
    }
}
