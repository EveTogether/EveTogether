using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunMiningEntryUnitsCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<SetRunMiningEntryUnitsCommand, Result>
{
    public Task<Result> Handle(SetRunMiningEntryUnitsCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Units < 1)
            return Task.FromResult(Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Units must be at least 1 — remove the line to take it off the run.", "Runs")));

        return RunMiningCorrections.ApplyAsync(contextFactory, eventBus, dispatcher, command.RunId, command.OreType,
            (entry, _) =>
            {
                entry.Units = command.Units;
                // Crit is already counted inside the units, so it can never be more than them.
                entry.CriticalUnits = Math.Min(entry.CriticalUnits, command.Units);
                return Result.Success();
            }, cancellationToken);
    }
}
