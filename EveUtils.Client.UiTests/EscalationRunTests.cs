using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-451: from a registered escalation to its run in one click, and the escalation ticked off at the run it
/// came from — by the escalation run's SAVE, or by hand.</summary>
public sealed class EscalationRunTests
{
    private const int ErvekamId = 30003867;
    private static readonly SdeSite RelayOutpost =
        new(2406, "Command Relay Outpost", null, "Escalation", null, "Sansha's Nation", null, 3, false, []);
    private static readonly SdeSite WarSupplyComplex =
        new(2405, "Sansha War Supply Complex", null, "Escalation", null, "Sansha's Nation", null, 3, false, []);

    [AvaloniaFact]
    public async Task EscalationRun_StartsFilledInFromItsSource_AndSavingItCompletesThatEscalationThere()
    {
        using var harness = await _CreateHarnessAsync();
        Guid sourceRunId = await _FlySourceRunAsync(harness, (RelayOutpost, "Ervekam"), (WarSupplyComplex, "Amamake"));
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        IReadOnlyList<OpenEscalationDto> open = await _OpenEscalationsAsync(dispatcher);
        Assert.Equal(2, open.Count);
        OpenEscalationDto relay = Assert.Single(open, row => row.Escalation.DungeonId == RelayOutpost.DungeonId);

        string? refused = await new EscalationRunStarter(dispatcher, harness.Dialogs, harness.Services).StartAsync(
            relay.SourceRunId, relay.CharacterId, ActivityWindowHarness.CharacterName, relay.Escalation);

        Assert.Null(refused);
        Run escalationRun = await _SingleRunAsync(harness, run => run.Id != sourceRunId);
        Assert.Equal((ActivityKind.Site, RelayOutpost.DungeonId, RelayOutpost.Name, ErvekamId, (long)ActivityWindowHarness.CharacterId),
            (escalationRun.ActivityKind, escalationRun.SiteTypeId, escalationRun.SiteName, escalationRun.SolarSystemId, escalationRun.CharacterId));
        Assert.Equal(escalationRun.Id,
            Assert.Single(await _OpenEscalationsAsync(dispatcher), row => row.InProgressRunId is not null).InProgressRunId);

        ActivityWindowViewModel window = harness.Dialogs.ShownActivityWindows.Last();
        await window.LoadAsync();
        await window.SaveRunCommand.ExecuteAsync(null);

        Assert.Equal(WarSupplyComplex.DungeonId, Assert.Single(await _OpenEscalationsAsync(dispatcher)).Escalation.DungeonId);
        EscalationEntryViewModel completed = Assert.Single((await _SourceDetailAsync(dispatcher, sourceRunId)).Escalation().Entries,
            entry => entry.Escalation.DungeonId == RelayOutpost.DungeonId);
        Assert.Equal((EscalationOutcome.Completed, escalationRun.Id),
            (completed.Escalation.Outcome, completed.Escalation.CompletedByRunId));
        Assert.True(completed.HasCompletedRun);
    }

    [AvaloniaFact]
    public async Task EscalationTickedOffByHand_LeavesTheOpenList_AndReopeningBringsItBack()
    {
        using var harness = await _CreateHarnessAsync();
        Guid sourceRunId = await _FlySourceRunAsync(harness, (RelayOutpost, "Ervekam"));
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        ActivityDetailViewModel detail = await _SourceDetailAsync(dispatcher, sourceRunId);

        await Assert.Single(detail.Escalation().Entries).MarkExpiredCommand.ExecuteAsync(null);

        Assert.Empty(await _OpenEscalationsAsync(dispatcher));
        EscalationEntryViewModel expired = Assert.Single((await _SourceDetailAsync(dispatcher, sourceRunId)).Escalation().Entries);
        Assert.Equal((EscalationOutcome.Expired, (Guid?)null), (expired.Escalation.Outcome, expired.Escalation.CompletedByRunId));

        await expired.ReopenCommand.ExecuteAsync(null);

        Assert.Single(await _OpenEscalationsAsync(dispatcher));
    }

    [AvaloniaFact]
    public async Task EscalationRun_ForAPilotAlreadyFlyingARun_IsRefused_AndStartsNothing()
    {
        using var harness = await _CreateHarnessAsync();
        await _FlySourceRunAsync(harness, (RelayOutpost, "Ervekam"));
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        OpenEscalationDto relay = Assert.Single(await _OpenEscalationsAsync(dispatcher));
        await dispatcher.Send(new StartRunCommand(ActivityWindowHarness.CharacterId, ActivityKind.Site, DateTime.UtcNow,
            0, "Sansha Hideaway", null, Origin: RunOrigin.Manual));

        string? refused = await new EscalationRunStarter(dispatcher, harness.Dialogs, harness.Services).StartAsync(
            relay.SourceRunId, relay.CharacterId, ActivityWindowHarness.CharacterName, relay.Escalation);

        Assert.NotNull(refused);
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await db.Set<Run>().CountAsync());
    }

    private static Task<ActivityWindowHarness> _CreateHarnessAsync()
    {
        var sde = new FakeSdeAccessor()
            .AddSolarSystem(new SdeSolarSystem(ErvekamId, "Ervekam", 0.69))
            .AddSite(RelayOutpost)
            .AddSite(WarSupplyComplex);
        return ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<ISdeAccessor>(sde));
    }

    /// <summary>A saved Sansha Refuge run with one escalation registered per site given, through the run window.</summary>
    private static async Task<Guid> _FlySourceRunAsync(
        ActivityWindowHarness harness, params (SdeSite Site, string System)[] escalations)
    {
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        Guid runId = model.RunId ?? throw new InvalidOperationException("The source run did not start.");
        foreach ((SdeSite site, string system) in escalations)
        {
            harness.Dialogs.OnShowEscalationDialog = dialog =>
            {
                dialog.SiteQuery = site.Name;
                dialog.SelectedOption = Assert.Single(dialog.SiteResults);
                dialog.DestinationSystem = system;
                dialog.RemainingTimeText = "23:00:00";
                dialog.RegisterCommand.Execute(null);
                return Task.FromResult(true);
            };
            await model.Activity().RegisterEscalationCommand.ExecuteAsync(null);
        }

        await model.SaveRunCommand.ExecuteAsync(null);
        return runId;
    }

    private static async Task<IReadOnlyList<OpenEscalationDto>> _OpenEscalationsAsync(IDispatcher dispatcher)
    {
        Result<IReadOnlyList<OpenEscalationDto>> open = await dispatcher.Query(
            new GetOpenEscalationsQuery(new HashSet<long> { ActivityWindowHarness.CharacterId }));
        return open.Value ?? throw new InvalidOperationException("The open escalations could not be read.");
    }

    private static async Task<ActivityDetailViewModel> _SourceDetailAsync(IDispatcher dispatcher, Guid sourceRunId)
    {
        Result<Guid?> found = await dispatcher.Query(new FindActivitySummaryIdQuery(null, sourceRunId));
        Guid activitySummaryId = found.Value ?? throw new InvalidOperationException("The source activity is missing.");
        var detail = new ActivityDetailViewModel(dispatcher, activitySummaryId);
        await detail.LoadAsync();
        return detail;
    }

    private static async Task<Run> _SingleRunAsync(ActivityWindowHarness harness, Func<Run, bool> which)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return Assert.Single(await db.Set<Run>().AsNoTracking().ToListAsync(), run => which(run));
    }
}
