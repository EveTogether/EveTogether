using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-499: a server copy that replaces one of the pilot's own synced runs must not take the loss links and the prices fixed
/// for them with it — neither is on the wire — and a loss that already lost its run to such a copy is matched again once.
/// </summary>
public sealed class RunSyncLossLinkTests
{
    private const int Pilot = 90000001;
    private const int Rifter = 587;
    private const int Capsule = 670;
    private const int Jita = 30000142;
    private static readonly DateTime StartedAtUtc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task PulledCopyOfASyncedRun_KeepsItsLossLinks_LinkSources_AndFixedPrices()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid runId = await _SavedSyncedRunAsync(instance);
        await _AddLossAsync(instance, 1, Rifter, runId, KillmailLinkSource.Auto);
        await _AddLossAsync(instance, 2, Capsule, runId, KillmailLinkSource.Auto);
        await _AddLossAsync(instance, 3, Rifter, runId, KillmailLinkSource.Manual);
        await _AddPriceAsync(instance, runId, 1, Rifter, 1_000m);
        await _AddPriceAsync(instance, runId, 2, Capsule, 100m);

        await _ApplyCopyOfAsync(instance, runId);

        Assert.Equal([(1, runId, KillmailLinkSource.Auto), (2, runId, KillmailLinkSource.Auto), (3, runId, KillmailLinkSource.Manual)],
            (await _LossesAsync(instance)).Select(loss => (loss.KillmailId, loss.RunId.GetValueOrDefault(), loss.LinkSource)));
        Assert.Equal([(1, Rifter, 1_000m), (2, Capsule, 100m)],
            (await _PricesAsync(instance, runId)).Select(price => (price.KillmailId, price.TypeId, price.UnitPriceIsk.GetValueOrDefault())));
    }

    [AvaloniaFact]
    public async Task PulledTombstone_StillUnlinksTheLossesOfTheRunItDeletes()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid runId = await _SavedSyncedRunAsync(instance);
        await _AddLossAsync(instance, 1, Rifter, runId, KillmailLinkSource.Auto);

        Run deleted = await _RunAsync(instance, runId);
        deleted.DeletedAtUtc = DateTime.UtcNow;
        await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync("https://server.example",
            [new RunWirePayload { Run = RunWireData.FromEntity(deleted), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
            new HashSet<Guid>(), Ct);

        Assert.Null(Assert.Single(await _LossesAsync(instance)).RunId);
    }

    [AvaloniaFact]
    public async Task Repair_MatchesAnAutoLinkedLossWithoutARunAgain_AndLeavesAnUnlinkAndAManualLinkAlone()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid runId = await _SavedSyncedRunAsync(instance);
        await _AddLossAsync(instance, 1, Rifter, null, KillmailLinkSource.Auto);
        await _AddLossAsync(instance, 2, Rifter, null, KillmailLinkSource.Manual);
        await _AddLossAsync(instance, 3, Rifter, null, KillmailLinkSource.None);
        var repair = instance.Services.GetRequiredService<OrphanedRunLinkRepair>();

        Assert.Equal(1, await repair.RepairAsync(Ct));
        Assert.Equal(0, await repair.RepairAsync(Ct));

        List<LocalKillmail> losses = await _LossesAsync(instance);
        Assert.Equal((runId, KillmailLinkSource.Auto), (losses[0].RunId.GetValueOrDefault(), losses[0].LinkSource));
        Assert.Equal((null, KillmailLinkSource.Manual), (losses[1].RunId, losses[1].LinkSource));
        Assert.Equal((null, KillmailLinkSource.None), (losses[2].RunId, losses[2].LinkSource));
    }

    [AvaloniaFact]
    public async Task Repair_LeavesALossNoRunHoldsUnlinked_AndDoesNotAskAgain()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _SavedSyncedRunAsync(instance);
        await _AddLossAsync(instance, 1, Rifter, null, KillmailLinkSource.Auto, StartedAtUtc.AddDays(3));
        var repair = instance.Services.GetRequiredService<OrphanedRunLinkRepair>();

        Assert.Equal(1, await repair.RepairAsync(Ct));
        Assert.Equal(0, await repair.RepairAsync(Ct));

        LocalKillmail loss = Assert.Single(await _LossesAsync(instance));
        Assert.Equal((null, KillmailLinkSource.None), (loss.RunId, loss.LinkSource));
    }

    private static async Task<Guid> _SavedSyncedRunAsync(TestClientInstance instance)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, StartedAtUtc, 1234, "Blood Refuge", Jita), Ct);
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), Ct)).IsSuccess);
        await using ClientDbContext db = await _DbAsync(instance);
        await db.Set<Run>().Where(run => run.Id == started.Value)
            .ExecuteUpdateAsync(set => set.SetProperty(run => run.SyncState, RunSyncState.Synced), Ct);
        return started.Value;
    }

    private static async Task _ApplyCopyOfAsync(TestClientInstance instance, Guid runId)
    {
        Run copy = await _RunAsync(instance, runId);
        copy.Revision++;
        await instance.Services.GetRequiredService<RunSynchronizationApplier>().ApplyAsync("https://server.example",
            [new RunWirePayload { Run = RunWireData.FromEntity(copy), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }],
            new HashSet<Guid>(), Ct);
    }

    private static async Task _AddLossAsync(TestClientInstance instance, int killmailId, int shipTypeId, Guid? runId,
        KillmailLinkSource source, DateTime? timeUtc = null)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        db.Set<LocalKillmail>().Add(new LocalKillmail
        {
            CharacterId = Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}",
            KillmailTimeUtc = timeUtc ?? StartedAtUtc.AddMinutes(5), SolarSystemId = Jita, IsLoss = true,
            VictimShipTypeId = shipTypeId, VictimCharacterId = Pilot, RunId = runId, LinkSource = source,
            ImportedAtUtc = StartedAtUtc.AddMinutes(6)
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task _AddPriceAsync(TestClientInstance instance, Guid runId, int killmailId, int typeId, decimal unitPrice)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        db.Set<RunLossPrice>().Add(new RunLossPrice
        {
            RunId = runId, CharacterId = Pilot, KillmailId = killmailId, TypeId = typeId, UnitPriceIsk = unitPrice,
            PricedAtUtc = DateTime.UtcNow, PriceSource = PriceSnapshotSource.Capture
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Ct);
    }

    private static async Task<List<LocalKillmail>> _LossesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<LocalKillmail>().AsNoTracking().OrderBy(killmail => killmail.KillmailId).ToListAsync(Ct);
    }

    private static async Task<List<RunLossPrice>> _PricesAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunLossPrice>().AsNoTracking().Where(price => price.RunId == runId)
            .OrderBy(price => price.KillmailId).ToListAsync(Ct);
    }

    private static Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Ct);
}
