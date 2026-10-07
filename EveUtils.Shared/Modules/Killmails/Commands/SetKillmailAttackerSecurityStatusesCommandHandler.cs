using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Shared.Modules.Killmails.Commands;

internal sealed class SetKillmailAttackerSecurityStatusesCommandHandler(ILocalKillmailRepository repository, IEventBus eventBus)
    : ICommandHandler<SetKillmailAttackerSecurityStatusesCommand, Result>
{
    public async Task<Result> Handle(SetKillmailAttackerSecurityStatusesCommand command, CancellationToken cancellationToken = default)
    {
        if (command.StatusesByKillmailId.Count == 0)
        {
            return Result.Success();
        }

        IReadOnlyList<int> characterIds = await repository.SetAttackerSecurityStatusesAsync(command.StatusesByKillmailId, cancellationToken);
        foreach (int characterId in characterIds)
        {
            await eventBus.PublishAsync(new KillmailsChangedEvent(characterId, KillmailsChangeKind.SecurityStatusFilled),
                EventTarget.Local, cancellationToken);
        }

        return Result.Success();
    }
}
