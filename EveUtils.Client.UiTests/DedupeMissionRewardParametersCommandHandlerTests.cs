using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-260: the one-time sweep for an existing mission group saved before the source fix, where every own
/// toon's run carried an identical copy of the same reward parameters. Keep-rule: the run with the most bounty keeps
/// them, everything else loses its copy and (if published) turns Outdated.</summary>
public sealed class DedupeMissionRewardParametersCommandHandlerTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<Guid> _StartAndSaveAsync(IDispatcher dispatcher, long characterId, string groupCode,
        decimal bountyIsk, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Mission, StartedAtUtc,
            4022, "Some Mission", 30000142, groupCode, SiteTypeSource: SiteTypeSource.Mission), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(5), Isk = bountyIsk }], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "1000000", Amount = 1_000_000m, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);
        return started.Value;
    }

    [AvaloniaFact]
    public async Task DuplicatedGroup_KeepsTheParametersOnTheRunWithTheMostBounty()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-WR43";
        Guid shooterRunId = await _StartAndSaveAsync(dispatcher, 90250177, groupCode, 29_976_751m, cancellationToken);
        Guid salvagerRunId = await _StartAndSaveAsync(dispatcher, 90382598, groupCode, 0m, cancellationToken);

        Result<int> repaired = await dispatcher.Send(new DedupeMissionRewardParametersCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.Equal(1, repaired.Value); // only the salvager's run lost its copy
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.NotEmpty(await db.Set<RunParameter>().Where(p => p.RunId == shooterRunId).ToListAsync(cancellationToken));
        Assert.Empty(await db.Set<RunParameter>().Where(p => p.RunId == salvagerRunId).ToListAsync(cancellationToken));
    }

    [AvaloniaFact]
    public async Task AlreadySyncedRun_TurnsOutdatedOnceItsDuplicateIsRemoved()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-WR44";
        await _StartAndSaveAsync(dispatcher, 90250177, groupCode, 5_000_000m, cancellationToken);
        Guid salvagerRunId = await _StartAndSaveAsync(dispatcher, 90382598, groupCode, 0m, cancellationToken);
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            Run salvagerRun = await db.Set<Run>().SingleAsync(run => run.Id == salvagerRunId, cancellationToken);
            salvagerRun.SyncState = RunSyncState.Synced;
            await db.SaveChangesAsync(cancellationToken);
        }

        await dispatcher.Send(new DedupeMissionRewardParametersCommand(), cancellationToken);

        await using ClientDbContext verify = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stored = await verify.Set<Run>().SingleAsync(run => run.Id == salvagerRunId, cancellationToken);
        Assert.Equal(RunSyncState.Outdated, stored.SyncState);
    }

    [AvaloniaFact]
    public async Task ARunAlreadyDownToOneWithParameters_IsLeftUntouched()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-WR45";
        // The post-source-fix shape: only one run in the group ever received parameters.
        Guid ownerRunId = await _StartAndSaveAsync(dispatcher, 90250177, groupCode, 5_000_000m, cancellationToken);
        Result<Guid> siblingStarted = await dispatcher.Send(new StartRunCommand(90382598, ActivityKind.Mission, StartedAtUtc,
            4022, "Some Mission", 30000142, groupCode, SiteTypeSource: SiteTypeSource.Mission), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(siblingStarted.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);

        Result<int> repaired = await dispatcher.Send(new DedupeMissionRewardParametersCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.Equal(0, repaired.Value);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.NotEmpty(await db.Set<RunParameter>().Where(p => p.RunId == ownerRunId).ToListAsync(cancellationToken));
    }

    [AvaloniaFact]
    public async Task ASoloMission_IsNeverTouched()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90250177, ActivityKind.Mission, StartedAtUtc,
            4022, "Some Mission", 30000142, SiteTypeSource: SiteTypeSource.Mission), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "1000000", Amount = 1_000_000m, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);

        Result<int> repaired = await dispatcher.Send(new DedupeMissionRewardParametersCommand(), cancellationToken);

        Assert.True(repaired.IsSuccess);
        Assert.Equal(0, repaired.Value);
    }
}
