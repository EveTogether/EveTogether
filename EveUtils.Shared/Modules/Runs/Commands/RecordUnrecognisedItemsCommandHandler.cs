using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RecordUnrecognisedItemsCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<RecordUnrecognisedItemsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RecordUnrecognisedItemsCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        HashSet<string> alreadyOpen = (await db.Set<UnrecognisedLootLine>()
                .Where(line => line.Source == UnrecognisedItemSource.Appraisal && line.Status == UnrecognisedItemStatus.Open)
                .Select(line => line.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DateTime nowUtc = DateTime.UtcNow;
        int added = 0;
        foreach (UnrecognisedLootNameInput name in command.Names.Where(name => !string.IsNullOrWhiteSpace(name.Name)))
        {
            if (!alreadyOpen.Add(name.Name))
                continue;

            db.Set<UnrecognisedLootLine>().Add(
                UnrecognisedLootWrites.NewLine(name, captureId: null, characterId: null, UnrecognisedItemSource.Appraisal, nowUtc));
            added++;
        }

        if (added > 0)
            await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(added);
    }
}
