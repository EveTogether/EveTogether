using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Shared.Modules.Settings.Commands;

internal sealed class DeleteSettingCommandHandler(ISettingRepository repository) : ICommandHandler<DeleteSettingCommand, Result>
{
    public async Task<Result> Handle(DeleteSettingCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Key))
        {
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Setting key is required.", "Settings"));
        }

        await repository.DeleteAsync(command.Key, cancellationToken);
        return Result.Success();
    }
}
