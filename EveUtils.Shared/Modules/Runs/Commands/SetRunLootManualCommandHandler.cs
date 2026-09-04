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
internal sealed class SetRunLootManualCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<SetRunLootManualCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SetRunLootManualCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Result<Run> opened = await RunLootWrites.OpenRunAsync(db, command.RunId, cancellationToken);
        if (!opened.IsSuccess)
            return Result<Guid>.Failure([.. opened.Messages]);

        List<RunLootCapture> captures = await db.Set<RunLootCapture>()
            .Where(candidate => candidate.RunId == command.RunId)
            .OrderBy(candidate => candidate.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        RunLootCapture? manual = captures.FirstOrDefault(candidate => candidate.Source is LootCaptureSource.Manual);
        if (manual is null)
        {
            manual = new RunLootCapture { Id = Guid.CreateVersion7(), RunId = command.RunId, Source = LootCaptureSource.Manual };
            db.Set<RunLootCapture>().Add(manual);
        }
        else
            // One statement rather than through the tracker: loaded and then cleared, the old rows still carry this
            // capture's id, and the fixup that follows from that writes them back instead of removing them.
            await db.Set<RunLootEntry>()
                .Where(entry => entry.RunLootCaptureId == manual.Id)
                .ExecuteDeleteAsync(cancellationToken);

        manual.CapturedAtUtc = command.CapturedAtUtc;
        manual.IsExcluded = false;
        // A written-out list is the loot itself, never one of the two cargo holds: a role it does not hold is a role
        // LootTally then reads off the captures behind it, and the difference would be counted twice.
        manual.Role = LootCaptureRole.Snapshot;
        foreach (RunLootEntryInput entry in command.Entries)
            db.Set<RunLootEntry>().Add(new RunLootEntry
            {
                Id = Guid.CreateVersion7(),
                RunLootCaptureId = manual.Id,
                ItemTypeId = entry.ItemTypeId,
                Name = entry.Name,
                Quantity = entry.Quantity,
                Volume = entry.Volume,
                ClipboardPrice = entry.ClipboardPrice,
                LootKind = entry.LootKind
            });

        // Excluded and not deleted: the captures the list was written from stay readable underneath it, which is the
        // only way to read back what the correction actually changed.
        foreach (RunLootCapture superseded in captures.Where(candidate => candidate.Id != manual.Id))
            superseded.IsExcluded = true;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunLootCapturedEvent(command.RunId), EventTarget.Local, cancellationToken);
        return Result<Guid>.Success(manual.Id);
    }
}
