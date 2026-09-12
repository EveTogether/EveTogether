using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-228: the one-time repair for a homefront started before the run window kept its dungeon id
/// (<c>ActivityWindowViewModel</c> always wrote <c>SiteTypeId: 0</c>). Guards the three things that matter — an
/// exact name repairs a run, a near-miss never does (AC-3: no guessing), and a published run's correction waits for
/// the next publish (ET-215's own rule) rather than riding along silently.</summary>
public sealed class RepairHomefrontSiteTypeIdsCommandHandlerTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string HomefrontName = "Raid: Hall of Sacrifice";
    private const int HomefrontDungeonId = 10347;

    private static FakeSdeAccessor _SdeWithHomefront() => new FakeSdeAccessor()
        .AddSite(new SdeSite(HomefrontDungeonId, HomefrontName, 70, "Homefront Operations", null, null, null, null, false, []));

    [AvaloniaFact]
    public async Task ExactNameMatch_RepairsTheStoredDungeonId()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(_SdeWithHomefront()));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = (await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 0,
            HomefrontName, 30000142), cancellationToken)).Value;

        Result<int> repaired = await dispatcher.Send(new RepairHomefrontSiteTypeIdsCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.Equal(1, repaired.Value);
        Assert.Equal(HomefrontDungeonId, (await _StoredAsync(instance, runId, cancellationToken)).SiteTypeId);
    }

    [AvaloniaFact]
    public async Task NoExactMatch_LeavesTheRunUntouched()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(_SdeWithHomefront()));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // A capitalisation slip, not the exact SDE name — AC-3's "no guessing" rule must leave this alone.
        Guid runId = (await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 0,
            "Raid: Hall Of Sacrifice", 30000142), cancellationToken)).Value;

        Result<int> repaired = await dispatcher.Send(new RepairHomefrontSiteTypeIdsCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.Equal(0, repaired.Value);
        Assert.Equal(0, (await _StoredAsync(instance, runId, cancellationToken)).SiteTypeId);
    }

    [AvaloniaFact]
    public async Task AlreadySyncedRun_TurnsOutdatedOnceRepaired()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(_SdeWithHomefront()));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid runId = (await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 0,
            HomefrontName, 30000142), cancellationToken)).Value;
        await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []),
            cancellationToken);
        IDbContextFactory<ClientDbContext> contextFactory =
            instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>();
        await using (ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            Run local = await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken);
            local.SyncState = RunSyncState.Synced;
            await db.SaveChangesAsync(cancellationToken);
        }

        await dispatcher.Send(new RepairHomefrontSiteTypeIdsCommand(), cancellationToken);

        Run stored = await _StoredAsync(instance, runId, cancellationToken);
        Assert.Equal(HomefrontDungeonId, stored.SiteTypeId);
        Assert.Equal(RunSyncState.Outdated, stored.SyncState);
    }

    [AvaloniaFact]
    public async Task Repairing_PublishesRunsChanged()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(_SdeWithHomefront()));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 0, HomefrontName, 30000142),
            cancellationToken);

        List<RunsChangedEventData> signalled = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => signalled.Add(changed.Data));
        Result<int> repaired = await dispatcher.Send(new RepairHomefrontSiteTypeIdsCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.NotEmpty(signalled);
    }

    private static async Task<Run> _StoredAsync(TestClientInstance instance, Guid runId, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken);
    }
}
