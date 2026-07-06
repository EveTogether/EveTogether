using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Repositories;

namespace EveUtils.Shared.Modules.Gamelog.Commands;

internal sealed class RecordCombatCommandHandler(IGamelogRepository repository, IPrincipalAccessor principals)
    : ICommandHandler<RecordCombatCommand, Result>
{
    public async Task<Result> Handle(RecordCombatCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var sample = new CombatSample
            {
                OwnerId = principals.Current.OwnerId, // pillar 4: stamp the owner from the current principal
                CharacterId = command.CharacterId,
                Timestamp = command.At,
                Amount = command.Amount,
                Direction = command.Direction,
                Target = command.Target
            };

            await repository.AddSampleAsync(sample, cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ServerError, $"Failed to record combat sample: {ex.Message}", "Gamelog"));
        }
    }
}
