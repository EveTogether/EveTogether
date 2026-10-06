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
        // A known copier (ET-138/ET-211) scopes the answer to their own run, so a second toon running its own site
        // is no longer "N runs are running" for loot that plainly has an owner. An unknown copier still needs one
        // unambiguous running run system-wide — ClipboardLootCapture is what asks before it gets here, when there is
        // more than one to choose between (the product decision, 2026-09-10).
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken, includeStopped: true,
            command.Capture.PreferredRunId, command.Capture.CharacterId);
        // A copy of nothing but names nobody knows yet (ET-460) is still worth keeping when no run can take it: the log
        // holds the names without a run, so they are there to price later.
        if (run is null && command.Capture.Entries.Count == 0 && command.Capture.UnrecognisedNames.Count > 0)
            return await _KeepWithoutARunAsync(db, command.Capture, cancellationToken);

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
        // A repeat is excluded from the totals, so its unknown names would be counted a second time by the log.
        if (repeatOf is null)
            foreach (UnrecognisedLootNameInput unrecognised in command.Capture.UnrecognisedNames)
                entity.UnrecognisedLines.Add(UnrecognisedLootWrites.NewLine(unrecognised, entity.Id,
                    command.Capture.CharacterId ?? run.CharacterId, UnrecognisedItemSource.ClipboardCapture,
                    command.Capture.CapturedAtUtc));
        db.Set<RunLootCapture>().Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        // Whoever is showing this run has to hear that it just gained loot. Storing the capture and telling the
        // player it was stored were two different things, and an activity window that was already open did neither:
        // the toast said "Loot copied" while the LOOT section under it went on reading "no loot captured".
        await eventBus.PublishAsync(new RunLootCapturedEvent(run.Id), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result<RunLootCaptureSaveResult>.Success(new RunLootCaptureSaveResult(entity.Id, repeatOf));
    }

    private static async Task<Result<RunLootCaptureSaveResult>> _KeepWithoutARunAsync(
        ClientDbContext db, RunLootCaptureInput capture, CancellationToken cancellationToken)
    {
        // The same copy again must not add its names a second time; a name and amount already open without a run is it.
        HashSet<(string Name, long Quantity)> kept = (await db.Set<UnrecognisedLootLine>()
                .Where(line => line.RunLootCaptureId == null && line.RunId == null
                               && line.Source == UnrecognisedItemSource.ClipboardCapture && line.Status == UnrecognisedItemStatus.Open)
                .Select(line => new { line.Name, line.Quantity })
                .ToListAsync(cancellationToken))
            .Select(line => (line.Name.ToUpperInvariant(), line.Quantity))
            .ToHashSet();
        foreach (UnrecognisedLootNameInput name in capture.UnrecognisedNames.Where(name => kept.Add((name.Name.ToUpperInvariant(), name.Quantity))))
            db.Set<UnrecognisedLootLine>().Add(UnrecognisedLootWrites.NewLine(name, captureId: null, capture.CharacterId,
                UnrecognisedItemSource.ClipboardCapture, capture.CapturedAtUtc));
        await db.SaveChangesAsync(cancellationToken);
        return Result<RunLootCaptureSaveResult>.Success(new RunLootCaptureSaveResult(Guid.Empty, null));
    }
}
