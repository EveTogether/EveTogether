using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

/// <summary>ET-472 follow-up: once, a published run of this machine's pilots that kept its combat is queued again, so its next
/// publish carries it. Only while the combat share is on; the marker is set either way, so it never runs twice.</summary>
public sealed class RunCombatRequeue(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher, IEventBus eventBus,
    ICharacterRegistry characters) : IScopedService
{
    public const string DoneSettingKey = "runs.combat-requeued";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        if (settings.Any(setting => setting.Key == DoneSettingKey))
        {
            return 0;
        }

        List<Guid> requeued = [];
        if (!settings.Any(setting => setting.Key == MetricShareSnapshot.CombatShareKey
                                     && string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase)))
        {
            long[] own = [.. (await characters.GetAllAsync(cancellationToken)).Select(character => character.EsiCharacterId).OfType<int>().Select(id => (long)id)];
            await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
            requeued = await db.Set<Run>().Where(run => run.SyncState == RunSyncState.Synced && own.Contains(run.CharacterId)
                    && db.Set<RunCombatTimeline>().Any(timeline => timeline.RunId == run.Id))
                .Select(run => run.Id).ToListAsync(cancellationToken);
            await db.Set<Run>().Where(run => requeued.Contains(run.Id))
                .ExecuteUpdateAsync(properties => properties.SetProperty(run => run.SyncState, RunSyncState.Pending), cancellationToken);
        }

        await dispatcher.Send(new SetSettingCommand(DoneSettingKey, "true"), cancellationToken);
        foreach (Guid runId in requeued)
        {
            await eventBus.PublishAsync(new RunsChangedEvent(runId), EventTarget.Local, cancellationToken);
        }

        return requeued.Count;
    }
}
