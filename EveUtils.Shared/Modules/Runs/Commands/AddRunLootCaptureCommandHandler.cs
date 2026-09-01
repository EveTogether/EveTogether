using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class AddRunLootCaptureCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<AddRunLootCaptureCommand, Result<DateTime?>>
{
    public async Task<Result<DateTime?>> Handle(AddRunLootCaptureCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Nothing names the run a clipboard copy belongs to, so one running run is the only unambiguous answer;
        // guessing between two would file loot under the wrong one.
        List<Run> running = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        if (running.Count == 0)
            return Result<DateTime?>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "No run is running, so this loot was not recorded.", "Runs"));
        if (running.Count > 1)
            return Result<DateTime?>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"{running.Count} runs are running, so this loot was not recorded against any of them.", "Runs"));

        Run run = running[0];
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
        return Result<DateTime?>.Success(repeatOf);
    }
}
