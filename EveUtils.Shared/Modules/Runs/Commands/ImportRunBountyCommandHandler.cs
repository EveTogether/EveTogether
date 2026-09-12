using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class ImportRunBountyCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher, ICharacterRegistry characters, IEventBus eventBus)
    : ICommandHandler<ImportRunBountyCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ImportRunBountyCommand command, CancellationToken cancellationToken = default)
    {
        Guid[] ids = [.. command.RunIds.Distinct()];
        if (ids.Length == 0)
            return Result<int>.Success(0);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .Where(run => ids.Contains(run.Id) && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return Result<int>.Success(0);

        Dictionary<long, string> names = await _NamesAsync(runs, cancellationToken);
        if (names.Count == 0)
            return Result<int>.Success(0);

        // Exclusive floor, the reader's own convention: a line on the very second the run started still counts.
        DateTime sinceUtc = runs.Min(run => run.StartedAtUtc).AddTicks(-1);
        DateTime untilUtc = runs.Max(run => run.StoppedAtUtc ?? DateTime.UtcNow);
        string directory = GameLogLocations.Resolve((await dispatcher.Query(new GetSettingsQuery(), cancellationToken))
            .FirstOrDefault(setting => setting.Key == GameLogLocations.DirectorySettingKey)?.Value);
        IReadOnlyDictionary<string, IReadOnlyList<GameLogEvent>> read =
            GameLogCatchUpReader.Read(directory, [.. names.Values.Distinct()], sinceUtc, untilUtc);

        long[] characterIds = [.. names.Keys];
        HashSet<(long CharacterId, long Ticks, decimal Isk)> recorded = [.. (await db.Set<RunBountyEntry>()
                .Join(db.Set<Run>(), entry => entry.RunId, run => run.Id,
                    (entry, run) => new { run.CharacterId, entry.OccurredAtUtc, entry.Isk })
                .Where(line => characterIds.Contains(line.CharacterId)
                               && line.OccurredAtUtc >= sinceUtc && line.OccurredAtUtc <= untilUtc)
                .ToListAsync(cancellationToken))
            .Select(line => (line.CharacterId, line.OccurredAtUtc.Ticks, line.Isk))];

        List<Run> changed = [];
        int added = 0;
        foreach (Run run in runs)
        {
            if (!names.TryGetValue(run.CharacterId, out string? name) || !read.TryGetValue(name, out IReadOnlyList<GameLogEvent>? events))
                continue;

            DateTime stopUtc = run.StoppedAtUtc ?? untilUtc;
            // Compared by value, like the reader: gamelog time is UTC without a kind of its own.
            foreach (BountyEvent bounty in events.OfType<BountyEvent>()
                         .Where(line => line.Timestamp >= run.StartedAtUtc && line.Timestamp <= stopUtc))
            {
                if (!recorded.Add((run.CharacterId, bounty.Timestamp.Ticks, bounty.Isk)))
                    continue;

                db.Set<RunBountyEntry>().Add(new RunBountyEntry
                {
                    Id = Guid.CreateVersion7(),
                    RunId = run.Id,
                    OccurredAtUtc = bounty.Timestamp,
                    Isk = bounty.Isk
                });
                added++;
                if (!changed.Contains(run))
                    changed.Add(run);
            }
        }

        if (changed.Count == 0)
            return Result<int>.Success(0);

        foreach (Run run in changed)
        {
            run.Revision++;
            if (run.SyncState is RunSyncState.Synced)
                run.SyncState = RunSyncState.Outdated;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (Run saved in changed.Where(run => run.State is RunState.Saved).DistinctBy(run => run.GroupCode ?? run.Id.ToString()))
            await dispatcher.Send(new RebuildActivitySummariesCommand(saved.Id), cancellationToken);
        foreach (Run run in changed)
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result<int>.Success(added);
    }

    // The name the run recorded when it was made (ET-212), or this client's own registry's — a gamelog is found by the
    // name in its header, never by id.
    private async Task<Dictionary<long, string>> _NamesAsync(IReadOnlyList<Run> runs, CancellationToken cancellationToken)
    {
        Dictionary<long, string> registered = [];
        foreach (Character character in await characters.GetAllAsync(cancellationToken))
            if (character.EsiCharacterId is { } characterId && !string.IsNullOrWhiteSpace(character.Name))
                registered.TryAdd(characterId, character.Name);

        Dictionary<long, string> names = [];
        foreach (Run run in runs)
            if ((run.CharacterNameSnapshot is { Length: > 0 } snapshot ? snapshot : registered.GetValueOrDefault(run.CharacterId))
                is { Length: > 0 } name)
                names.TryAdd(run.CharacterId, name);
        return names;
    }
}
