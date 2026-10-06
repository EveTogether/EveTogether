using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>How the commands that take a list of item names keep the ones no SDE type carries (ET-460).</summary>
internal static class UnrecognisedLootWrites
{
    public static UnrecognisedLootLine NewLine(UnrecognisedLootNameInput name, Guid? captureId, long? characterId,
        UnrecognisedItemSource source, DateTime seenAtUtc) => new()
    {
        Id = Guid.CreateVersion7(),
        RunLootCaptureId = captureId,
        CharacterId = characterId,
        Source = source,
        Name = name.Name,
        Quantity = name.Quantity,
        FirstSeenAtUtc = seenAtUtc,
        Status = UnrecognisedItemStatus.Open
    };

    /// <summary>For a list the pilot rewrites without ever seeing these names in it: what is already open on the capture
    /// stays, and only names not yet there are added, so retyping the list cannot drop one from the log.</summary>
    public static async Task AddOpenAsync(ClientDbContext db, Guid captureId, IReadOnlyList<UnrecognisedLootNameInput> names,
        long characterId, DateTime seenAtUtc, CancellationToken cancellationToken)
    {
        HashSet<string> alreadyOpen = (await db.Set<UnrecognisedLootLine>()
                .Where(line => line.RunLootCaptureId == captureId && line.Status == UnrecognisedItemStatus.Open)
                .Select(line => line.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (UnrecognisedLootNameInput name in names.Where(name => alreadyOpen.Add(name.Name)))
            db.Set<UnrecognisedLootLine>().Add(NewLine(name, captureId, characterId, UnrecognisedItemSource.RunWindowEntry, seenAtUtc));
    }

    /// <summary>A rewritten box replaces what it held before, so the names it still cannot read replace the open ones of
    /// its capture. A line already recognised is history and stays.</summary>
    public static async Task ReplaceOpenAsync(ClientDbContext db, Guid captureId, IReadOnlyList<UnrecognisedLootNameInput>? names,
        long characterId, DateTime seenAtUtc, CancellationToken cancellationToken)
    {
        await db.Set<UnrecognisedLootLine>()
            .Where(line => line.RunLootCaptureId == captureId && line.Status == UnrecognisedItemStatus.Open)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (UnrecognisedLootNameInput name in names ?? [])
            db.Set<UnrecognisedLootLine>().Add(NewLine(name, captureId, characterId, UnrecognisedItemSource.RunWindowEntry, seenAtUtc));
    }
}
