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
internal sealed class AddRunLootCaptureCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<AddRunLootCaptureCommand, Result<RunLootCaptureSaveResult>>
{
    public async Task<Result<RunLootCaptureSaveResult>> Handle(AddRunLootCaptureCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Nothing names the run a clipboard copy belongs to, so one running run is the only unambiguous answer;
        // guessing between two would file loot under the wrong one.
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken, includeStopped: true,
            command.Capture.PreferredRunId);
        if (run is null)
            return Result<RunLootCaptureSaveResult>.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                    "No run is running, so this loot was not recorded.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{runningCount} runs are running, so this loot was not recorded against any of them.", "Runs"));

        DateTime? repeatOf = command.Capture.ContentHash is not { } hash
            ? null
            : await db.Set<RunLootCapture>()
                .Where(capture => capture.RunId == run.Id && capture.ContentHash == hash)
                .OrderBy(capture => capture.CapturedAtUtc)
                .Select(capture => (DateTime?)capture.CapturedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

        var entity = new RunLootCapture
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            CapturedAtUtc = command.Capture.CapturedAtUtc,
            Source = command.Capture.Source,
            Role = command.Capture.Role,
            ContentHash = command.Capture.ContentHash,
            IsExcluded = repeatOf is not null
        };
        foreach (RunLootEntryInput entry in command.Capture.Entries)
            entity.Entries.Add(new RunLootEntry
            {
                Id = Guid.CreateVersion7(),
                ItemTypeId = entry.ItemTypeId,
                Name = entry.Name,
                Quantity = entry.Quantity,
                Volume = entry.Volume,
                ClipboardPrice = entry.ClipboardPrice,
                LootKind = entry.LootKind
            });
        db.Set<RunLootCapture>().Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        // Whoever is showing this run has to hear that it just gained loot. Storing the capture and telling the
        // player it was stored were two different things, and an activity window that was already open did neither:
        // the toast said "Loot copied" while the LOOT section under it went on reading "no loot captured".
        await eventBus.PublishAsync(new RunLootCapturedEvent(run.Id), EventTarget.Local, cancellationToken);
        return Result<RunLootCaptureSaveResult>.Success(new RunLootCaptureSaveResult(entity.Id, repeatOf));
    }
}
