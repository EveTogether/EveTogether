using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
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

/// <summary>ET-490: a run started by hand from the site list was stored without a system. The start now asks for the
/// pilot's current system, the save fills one that was still unknown, and a one-time correction fills saved runs whose
/// group agrees on a single system.</summary>
public sealed class RunSolarSystemTests
{
    private const int PilotId = 90000002;
    private const int SystemId = 30005035;
    private static readonly DateTime StartedAtUtc = new(2026, 10, 9, 11, 17, 0, DateTimeKind.Utc);
    private static readonly SdeSite Site = new(13406, "Tetrimon Garrison", null, null, null, null, null, null, false, []);

    private static TestClientInstance CreateInstance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddSite(Site)));

    private static ManualRunStartViewModel CreateViewModel(TestClientInstance instance, ICharacterSystemLocator? locator) =>
        new(instance.Services.GetRequiredService<IDispatcher>(),
            instance.Services.GetRequiredService<ISdeAccessor>(),
            new RecordingDialogService(),
            kind => new ActivityWindowViewModel(kind, instance.Services),
            [new Character("Manual Pilot", PilotId)],
            systemLocator: locator) { SelectedOption = new SdeSitePickerOption(Site, Site.Name) };

    [AvaloniaFact]
    public async Task ManualStart_WithAKnownLocation_StoresTheSystem()
    {
        using var instance = CreateInstance();
        var vm = CreateViewModel(instance, new FixedLocator(SystemId));

        await vm.StartCommand.ExecuteAsync(null);

        Run run = await _SingleRunAsync(instance);
        Assert.Equal(RunOrigin.Manual, run.Origin);
        Assert.Equal(SystemId, run.SolarSystemId);
    }

    [AvaloniaFact]
    public async Task ManualStart_WithNoKnownLocation_StoresNoSystemYet()
    {
        using var instance = CreateInstance();
        var vm = CreateViewModel(instance, new FixedLocator(null));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Null((await _SingleRunAsync(instance)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Save_FillsASystemTheStartDidNotKnow()
    {
        using var instance = CreateInstance();
        Guid runId = await _StartedRunAsync(instance, solarSystemId: null);

        Result saved = await _SaveAsync(instance, runId, SystemId);

        Assert.True(saved.IsSuccess);
        Assert.Equal(SystemId, (await _SingleRunAsync(instance)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Save_KeepsTheSystemTheRunWasStartedWith()
    {
        using var instance = CreateInstance();
        Guid runId = await _StartedRunAsync(instance, solarSystemId: SystemId);

        await _SaveAsync(instance, runId, solarSystemId: 30000142);

        Assert.Equal(SystemId, (await _SingleRunAsync(instance)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Fill_TakesTheSystemTheGroupAgreesOn_AndIsIdempotent()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid known = await _StartedGroupRunAsync(instance, 90000001, "G1", SystemId);
        Guid unknown = await _StartedGroupRunAsync(instance, PilotId, "G1", solarSystemId: null);
        await _SaveAsync(instance, known, solarSystemId: null);
        await _SaveAsync(instance, unknown, solarSystemId: null);

        Result<int> first = await dispatcher.Send(new FillRunSolarSystemsCommand(), cancellationToken);
        Result<int> second = await dispatcher.Send(new FillRunSolarSystemsCommand(), cancellationToken);

        Assert.Equal(1, first.Value);
        Assert.Equal(0, second.Value);
        Assert.Equal(SystemId, (await _RunAsync(instance, unknown)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Fill_LeavesARunAloneWhenItsGroupDisagrees()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid first = await _StartedGroupRunAsync(instance, 90000001, "G1", SystemId);
        Guid second = await _StartedGroupRunAsync(instance, 90000003, "G1", 30000142);
        Guid unknown = await _StartedGroupRunAsync(instance, PilotId, "G1", solarSystemId: null);
        foreach (Guid runId in new[] { first, second, unknown })
            await _SaveAsync(instance, runId, solarSystemId: null);

        Result<int> filled = await dispatcher.Send(new FillRunSolarSystemsCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(0, filled.Value);
        Assert.Null((await _RunAsync(instance, unknown)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Fill_LeavesARunWithoutAGroupAlone()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartedRunAsync(instance, solarSystemId: null);
        await _SaveAsync(instance, runId, solarSystemId: null);

        Result<int> filled = await dispatcher.Send(new FillRunSolarSystemsCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(0, filled.Value);
        Assert.Null((await _SingleRunAsync(instance)).SolarSystemId);
    }

    [AvaloniaFact]
    public async Task Fill_TurnsAPublishedRunOutdated_AndSignalsRunsChanged()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid known = await _StartedGroupRunAsync(instance, 90000001, "G1", SystemId);
        Guid unknown = await _StartedGroupRunAsync(instance, PilotId, "G1", solarSystemId: null);
        await _SaveAsync(instance, known, solarSystemId: null);
        await _SaveAsync(instance, unknown, solarSystemId: null);
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
                         .CreateDbContextAsync(cancellationToken))
        {
            (await db.Set<Run>().SingleAsync(run => run.Id == unknown, cancellationToken)).SyncState = RunSyncState.Synced;
            await db.SaveChangesAsync(cancellationToken);
        }

        List<RunsChangedEventData> signalled = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => signalled.Add(changed.Data));
        await dispatcher.Send(new FillRunSolarSystemsCommand(), cancellationToken);

        Assert.Equal(RunSyncState.Outdated, (await _RunAsync(instance, unknown)).SyncState);
        Assert.NotEmpty(signalled);
    }

    private static async Task<Guid> _StartedRunAsync(TestClientInstance instance, int? solarSystemId) =>
        (await instance.Services.GetRequiredService<IDispatcher>().Send(new StartRunCommand(PilotId, ActivityKind.Site,
            StartedAtUtc, Site.DungeonId, Site.Name, solarSystemId, Origin: RunOrigin.Manual),
            TestContext.Current.CancellationToken)).Value;

    private static async Task<Guid> _StartedGroupRunAsync(TestClientInstance instance, int characterId, string groupCode,
        int? solarSystemId) =>
        (await instance.Services.GetRequiredService<IDispatcher>().Send(new StartRunCommand(characterId, ActivityKind.Site,
            StartedAtUtc, Site.DungeonId, Site.Name, solarSystemId, GroupCode: groupCode, Origin: RunOrigin.Manual),
            TestContext.Current.CancellationToken)).Value;

    private static Task<Result> _SaveAsync(TestClientInstance instance, Guid runId, int? solarSystemId) =>
        instance.Services.GetRequiredService<IDispatcher>().Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(20),
            StartedAtUtc.AddMinutes(21), [], [], [], [], SolarSystemId: solarSystemId), TestContext.Current.CancellationToken);

    private static async Task<Run> _SingleRunAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Set<Run>().SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Set<Run>().SingleAsync(run => run.Id == runId, TestContext.Current.CancellationToken);
    }

    private sealed class FixedLocator(int? solarSystemId) : ICharacterSystemLocator
    {
        public int? SolarSystemIdOf(int characterId) => solarSystemId;
    }
}
