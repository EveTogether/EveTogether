using System.Globalization;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunConsumablesManualCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<SetRunConsumablesManualCommand, Result>
{
    public async Task<Result> Handle(SetRunConsumablesManualCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Result<Run> opened = await RunLootWrites.OpenForCorrectionAsync(db, command.RunId, cancellationToken);
        if (!opened.IsSuccess || opened.Value is not { } run)
            return Result.Failure([.. opened.Messages]);

        // Until SAVE the run window owns the filament count and writes it then; a count stored here first would be a
        // second answer SAVE never reads.
        if (run.State is not RunState.Saved)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This run is not saved yet; its filament count is set in the run window until SAVE.", "Runs"));

        RunLootWrites.MarkCorrected(run);
        DateTime nowUtc = DateTime.UtcNow;
        List<RunParameter> parameters = await db.Set<RunParameter>()
            .Where(parameter => parameter.RunId == command.RunId)
            .ToListAsync(cancellationToken);
        int? filamentTypeId = RunIskFactsReader.FilamentTypeId(parameters);
        if (filamentTypeId is { } typeId)
            _SetFilamentCount(db, parameters, command.RunId, nowUtc, command.Entries
                .Where(entry => entry.ItemTypeId == typeId)
                .Sum(entry => entry.Quantity ?? 1));

        RunLootEntryInput[] spent = [.. command.Entries.Where(entry => entry.ItemTypeId != filamentTypeId)];
        await _ReplaceSpentAsync(db, run, spent, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(command.RunId), cancellationToken);
        await eventBus.PublishAsync(new RunLootCorrectedEvent(command.RunId), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }

    private static void _SetFilamentCount(ClientDbContext db, List<RunParameter> parameters, Guid runId, DateTime nowUtc,
        long count)
    {
        RunParameter? stored = parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentCount);
        if (stored is null)
        {
            stored = new RunParameter { Id = Guid.CreateVersion7(), RunId = runId, ParameterKey = RunParameterKey.AbyssalFilamentCount };
            db.Set<RunParameter>().Add(stored);
        }

        stored.TypedValue = count.ToString(CultureInfo.InvariantCulture);
        stored.ObservedAtUtc = nowUtc;
    }

    private static async Task _ReplaceSpentAsync(ClientDbContext db, Run run, RunLootEntryInput[] spent,
        CancellationToken cancellationToken)
    {
        List<RunLootCapture> captures = await db.Set<RunLootCapture>()
            .Where(capture => capture.RunId == run.Id)
            .ToListAsync(cancellationToken);
        RunLootCapture? consumed = captures.FirstOrDefault(capture => capture.Role is LootCaptureRole.Consumed);
        if (consumed is not null)
            // One statement rather than through the tracker, for the reason SetRunLootManualCommandHandler gives.
            await db.Set<RunLootEntry>()
                .Where(entry => entry.RunLootCaptureId == consumed.Id)
                .ExecuteDeleteAsync(cancellationToken);

        if (spent.Length == 0)
        {
            if (consumed is not null)
                db.Set<RunLootCapture>().Remove(consumed);
            return;
        }

        if (consumed is null)
        {
            consumed = new RunLootCapture
            {
                Id = Guid.CreateVersion7(), RunId = run.Id, Source = LootCaptureSource.Manual, Role = LootCaptureRole.Consumed
            };
            db.Set<RunLootCapture>().Add(consumed);
        }

        // Ahead of every capture the run holds: a client from before this role reads it as one more snapshot — spent loot
        // where every capture counts, and too early to be the hold a cargo difference ends on.
        consumed.CapturedAtUtc = captures
            .Where(capture => capture.Id != consumed.Id)
            .Select(capture => capture.CapturedAtUtc)
            .Append(run.StartedAtUtc)
            .Min()
            .AddSeconds(-1);
        consumed.IsExcluded = false;
        foreach (RunLootEntryInput entry in spent)
            db.Set<RunLootEntry>().Add(new RunLootEntry
            {
                Id = Guid.CreateVersion7(),
                RunLootCaptureId = consumed.Id,
                ItemTypeId = entry.ItemTypeId,
                Name = entry.Name,
                // No count typed means one of it — the reading the list on screen takes, so the stored cost agrees.
                Quantity = entry.Quantity ?? 1,
                Volume = entry.Volume,
                ClipboardPrice = entry.ClipboardPrice,
                LootKind = LootKind.Lost
            });
    }
}
