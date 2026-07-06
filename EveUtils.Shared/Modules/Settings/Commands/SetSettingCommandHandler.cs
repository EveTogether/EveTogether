using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Shared.Modules.Settings.Commands;

internal sealed class SetSettingCommandHandler(ISettingRepository repository) : ICommandHandler<SetSettingCommand, Result>
{
    public async Task<Result> Handle(SetSettingCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Key))
        {
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Setting key is required.", "Settings"));
        }

        await repository.UpsertAsync(command.Key, command.Value, cancellationToken);
        return Result.Success();
    }
}
