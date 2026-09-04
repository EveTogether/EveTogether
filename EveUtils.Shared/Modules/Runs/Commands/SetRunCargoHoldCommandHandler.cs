using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunCargoHoldCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<SetRunCargoHoldCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SetRunCargoHoldCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue, cancellationToken);
        if (run is null)
            return Result<Guid>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        // Saving is the lock. The cargo holds stay adjustable until then and not a moment after — including on the
        // run ET-179 finished on the pilot's behalf a day after STOP.
        if (run.State is RunState.Saved)
            return Result<Guid>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This run is saved, so its loot can no longer be changed.", "Runs"));

        // The box only ever rewrites what the box itself wrote: a clipboard capture that happens to hold the role
        // right now was really copied out of EVE, and typing here must not overwrite it.
        RunLootCapture? capture = await db.Set<RunLootCapture>()
            .Where(candidate => candidate.RunId == run.Id && candidate.Source == LootCaptureSource.Pasted && candidate.Role == command.Role)
            .OrderBy(candidate => candidate.CapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (capture is null)
        {
            capture = new RunLootCapture { Id = Guid.CreateVersion7(), RunId = run.Id, Source = LootCaptureSource.Pasted };
            db.Set<RunLootCapture>().Add(capture);
        }
        else
            // The old rows go in one statement rather than through the tracker: loaded and then cleared, they still
            // carry this capture's id, and the fixup that follows from that writes them back instead of removing them.
            await db.Set<RunLootEntry>()
                .Where(entry => entry.RunLootCaptureId == capture.Id)
                .ExecuteDeleteAsync(cancellationToken);

        capture.CapturedAtUtc = command.CapturedAtUtc;
        capture.IsExcluded = false;
        // Added to the set and not to the navigation: a row hung on a capture that is already in the database comes
        // out of the tracker as an update of a row that was never there, because its key is already filled in.
        foreach (RunLootEntryInput entry in command.Entries)
            db.Set<RunLootEntry>().Add(new RunLootEntry
            {
                Id = Guid.CreateVersion7(),
                RunLootCaptureId = capture.Id,
                ItemTypeId = entry.ItemTypeId,
                Name = entry.Name,
                Quantity = entry.Quantity,
                Volume = entry.Volume,
                ClipboardPrice = entry.ClipboardPrice,
                LootKind = entry.LootKind
            });
        await RunLootCaptureRoles.AssignAsync(db, capture, command.Role, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunLootCapturedEvent(run.Id), EventTarget.Local, cancellationToken);
        return Result<Guid>.Success(capture.Id);
    }
}
