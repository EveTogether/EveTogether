using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class SetKillmailPositionsCommandHandler(ILocalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<SetKillmailPositionsCommand, Result>
{
    public async Task<Result> Handle(SetKillmailPositionsCommand command, CancellationToken cancellationToken = default)
    {
        if (command.PositionsByKillmailId.Count == 0)
        {
            return Result.Success();
        }

        IReadOnlyList<int> characterIds = await repository.SetPositionsAsync(command.PositionsByKillmailId, cancellationToken);
        foreach (int characterId in characterIds)
        {
            await eventBus.PublishAsync(new KillmailsChangedEvent(characterId, KillmailsChangeKind.PositionFilled),
                EventTarget.Local, cancellationToken);
        }

        return Result.Success();
    }
}
