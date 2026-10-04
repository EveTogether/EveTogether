using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RemoveRunMiningEntryCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<RemoveRunMiningEntryCommand, Result>
{
    public Task<Result> Handle(RemoveRunMiningEntryCommand command, CancellationToken cancellationToken = default) =>
        RunMiningCorrections.ApplyAsync(contextFactory, eventBus, dispatcher, command.RunId, command.OreType,
            (entry, db) =>
            {
                db.Set<RunMiningEntry>().Remove(entry);
                return Result.Success();
            }, cancellationToken);
}
