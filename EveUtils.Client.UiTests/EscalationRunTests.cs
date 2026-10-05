using Avalonia.Headless.XUnit;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
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
        new(2406, "Command Relay Outpost", null, "Escalation", 500019, "Sansha's Nation", null, 3, false, []);
    private static readonly SdeSite WarSupplyComplex =
        new(2405, "Sansha War Supply Complex", null, "Escalation", 500019, "Sansha's Nation", null, 3, false, []);
    private static readonly SdeSite AngelShipyard =
        new(2396, "Angel Cartel Naval Shipyard", null, "Escalation", 500011, "Angel Cartel", null, 4, false, []);

    [AvaloniaFact]
    public async Task EscalationRun_StartsFilledInFromItsSource_AndSavingItCompletesThatEscalationThere()
    {
        using var harness = await _CreateHarnessAsync();
        Guid sourceRunId = await _FlySourceRunAsync(harness, "Sansha Refuge",
            (RelayOutpost, "Ervekam", "23:00:00"), (WarSupplyComplex, "Amamake", "23:00:00"));
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
        Guid sourceRunId = await _FlySourceRunAsync(harness, "Sansha Refuge", (RelayOutpost, "Ervekam", "23:00:00"));
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
        await _FlySourceRunAsync(harness, "Sansha Refuge", (RelayOutpost, "Ervekam", "23:00:00"));
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

    /// <summary>
    /// The pre-merge walkthrough (Jithran, 2026-10-06): an evening of sites as a pilot flies it, through the screens
    /// a pilot uses — the run window, OPEN ESCALATIONS and the manual start. Two sources with three escalations, the
    /// soonest deadline listed first; one flown from the band and ticked off at its source; one past its deadline and
    /// gone from the band; an unregistered escalation started by hand; a discarded escalation run leaving its
    /// escalation open; an escalation run that escalates again; and a deleted source taking its escalations along.
    /// </summary>
    [AvaloniaFact]
    public async Task EveningOfSites_FlowsFromRegistrationThroughTheBandToCompletion()
    {
        using var harness = await _CreateHarnessAsync();
        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Guid refugeRunId = await _FlySourceRunAsync(harness, "Sansha Refuge", (RelayOutpost, "Ervekam", "10:00:00"));
        Guid hideawayRunId = await _FlySourceRunAsync(harness, "Angel Hideaway",
            (WarSupplyComplex, "Amamake", "20:00:00"), (AngelShipyard, "Ervekam", "5:00:00"));

        RunsOverviewViewModel band = await _BandAsync(harness);
        Assert.Equal([AngelShipyard.DungeonId, RelayOutpost.DungeonId, WarSupplyComplex.DungeonId],
            band.OpenEscalations.Select(row => row.Escalation.Escalation.DungeonId));
        Assert.All(band.OpenEscalations, row => Assert.True(OpsecText.IsMarked(row.EscalationSiteText)));

        await band.OpenEscalations[0].StartCommand.ExecuteAsync(null);
        Run shipyardRun = await _SingleRunAsync(harness, run => run.SiteTypeId == AngelShipyard.DungeonId);
        Assert.Equal((AngelShipyard.Name, ErvekamId, RunState.Running),
            (shipyardRun.SiteName, shipyardRun.SolarSystemId, shipyardRun.State));
        await _SaveShownWindowAsync(harness);

        Assert.Equal([RelayOutpost.DungeonId, WarSupplyComplex.DungeonId],
            (await _BandAsync(harness)).OpenEscalations.Select(row => row.Escalation.Escalation.DungeonId));
        Assert.Equal((EscalationOutcome.Completed, shipyardRun.Id), await _OutcomeAsync(dispatcher, hideawayRunId, AngelShipyard));

        await _PassDeadlineAsync(harness, WarSupplyComplex);
        Assert.Equal([RelayOutpost.DungeonId],
            (await _BandAsync(harness)).OpenEscalations.Select(row => row.Escalation.Escalation.DungeonId));
        Assert.Equal("past its deadline", Assert.Single((await _SourceDetailAsync(dispatcher, hideawayRunId)).Escalation().Entries,
            entry => entry.Escalation.DungeonId == WarSupplyComplex.DungeonId).StatusText);

        var manualStart = new ManualRunStartViewModel(dispatcher, harness.Services.GetRequiredService<ISdeAccessor>(),
            harness.Dialogs, kind => new ActivityWindowViewModel(kind, harness.Services),
            [new Character(ActivityWindowHarness.CharacterName, ActivityWindowHarness.CharacterId)]);
        await manualStart.LoadAsync();
        manualStart.SiteQuery = "War Supply";
        manualStart.SelectedOption = Assert.Single(manualStart.SiteResults);
        await manualStart.StartCommand.ExecuteAsync(null);
        Assert.Equal(SiteTypeSource.Site,
            (await _SingleRunAsync(harness, run => run.SiteTypeId == WarSupplyComplex.DungeonId)).SiteTypeSource);
        await _SaveShownWindowAsync(harness);

        await (await _BandAsync(harness)).OpenEscalations[0].StartCommand.ExecuteAsync(null);
        Run discarded = await _SingleRunAsync(harness, run => run.SiteTypeId == RelayOutpost.DungeonId);
        await dispatcher.Send(new DiscardRunCommand(discarded.Id, DateTime.UtcNow, DeleteAfterDiscard: true));
        OpenEscalationRowViewModel relayAgain = Assert.Single((await _BandAsync(harness)).OpenEscalations);
        Assert.True(relayAgain.CanStart);

        await relayAgain.StartCommand.ExecuteAsync(null);
        ActivityWindowViewModel relayWindow = harness.Dialogs.ShownActivityWindows.Last();
        await relayWindow.LoadAsync();
        List<int> offeredBeforeTyping = [];
        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            offeredBeforeTyping.AddRange(dialog.SiteResults.Select(option => option.Site.DungeonId));
            dialog.SiteQuery = WarSupplyComplex.Name;
            dialog.SelectedOption = Assert.Single(dialog.SiteResults);
            dialog.DestinationSystem = "Amamake";
            dialog.RemainingTimeText = "15:00:00";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };
        await relayWindow.Activity().RegisterEscalationCommand.ExecuteAsync(null);
        await relayWindow.SaveRunCommand.ExecuteAsync(null);
        Guid relayRunId = relayWindow.RunId ?? throw new InvalidOperationException("The relay run has no id.");
        // The escalation run's own site is Sansha's, so Sansha's escalations come first even though its window adopted
        // the run and never matched a copied name.
        Assert.Equal([RelayOutpost.DungeonId, WarSupplyComplex.DungeonId, AngelShipyard.DungeonId], offeredBeforeTyping);

        Assert.Equal(EscalationOutcome.Completed, (await _OutcomeAsync(dispatcher, refugeRunId, RelayOutpost)).Outcome);
        OpenEscalationRowViewModel chained = Assert.Single((await _BandAsync(harness)).OpenEscalations);
        Assert.Equal((relayRunId, WarSupplyComplex.DungeonId),
            (chained.Escalation.SourceRunId, chained.Escalation.Escalation.DungeonId));

        await dispatcher.Send(new DeleteRunCommand(relayRunId, DateTime.UtcNow));
        Assert.Empty((await _BandAsync(harness)).OpenEscalations);
    }

    private static async Task<RunsOverviewViewModel> _BandAsync(ActivityWindowHarness harness)
    {
        var overview = new RunsOverviewViewModel(harness.Services.GetRequiredService<IDispatcher>(), harness.Dialogs,
            harness.Services, [new Character(ActivityWindowHarness.CharacterName, ActivityWindowHarness.CharacterId)],
            runClock: false);
        await overview.LoadAsync(TestContext.Current.CancellationToken);
        return overview;
    }

    /// <summary>Saves the run in the window the last start opened, as its pilot would.</summary>
    private static async Task _SaveShownWindowAsync(ActivityWindowHarness harness)
    {
        ActivityWindowViewModel window = harness.Dialogs.ShownActivityWindows.Last();
        await window.LoadAsync();
        await window.SaveRunCommand.ExecuteAsync(null);
    }

    /// <summary>Time passing, without waiting for it: the escalation's stored deadline moved into the past.</summary>
    private static async Task _PassDeadlineAsync(ActivityWindowHarness harness, SdeSite escalation)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Guid entryId = (await db.Set<RunParameter>().SingleAsync(parameter =>
                parameter.ParameterKey == RunParameterKey.EscalationDungeonId
                && parameter.TypedValue == escalation.DungeonId.ToString()))
            .EntryId ?? throw new InvalidOperationException("The escalation has no entry.");
        RunParameter deadline = await db.Set<RunParameter>().SingleAsync(parameter =>
            parameter.ParameterKey == RunParameterKey.EscalationExpiresAtUtc && parameter.EntryId == entryId);
        deadline.TypedValue = DateTime.UtcNow.AddMinutes(-1).ToString("o");
        await db.SaveChangesAsync();
    }

    private static async Task<(EscalationOutcome? Outcome, Guid? CompletedByRunId)> _OutcomeAsync(
        IDispatcher dispatcher, Guid sourceRunId, SdeSite escalation)
    {
        EscalationEntryViewModel entry = Assert.Single((await _SourceDetailAsync(dispatcher, sourceRunId)).Escalation().Entries,
            candidate => candidate.Escalation.DungeonId == escalation.DungeonId);
        return (entry.Escalation.Outcome, entry.Escalation.CompletedByRunId);
    }

    private static Task<ActivityWindowHarness> _CreateHarnessAsync()
    {
        var sde = new FakeSdeAccessor()
            .AddSolarSystem(new SdeSolarSystem(ErvekamId, "Ervekam", 0.69))
            .AddSite(RelayOutpost)
            .AddSite(WarSupplyComplex)
            .AddSite(AngelShipyard);
        return ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<ISdeAccessor>(sde));
    }

    /// <summary>A saved run of <paramref name="sourceName"/> with one escalation registered per site given, each with the
    /// remaining time the Agency showed, through the run window.</summary>
    private static async Task<Guid> _FlySourceRunAsync(
        ActivityWindowHarness harness, string sourceName, params (SdeSite Site, string System, string Remaining)[] escalations)
    {
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = sourceName;
        await model.StartRunCommand.ExecuteAsync(null);
        Guid runId = model.RunId ?? throw new InvalidOperationException("The source run did not start.");
        foreach ((SdeSite site, string system, string remaining) in escalations)
        {
            harness.Dialogs.OnShowEscalationDialog = dialog =>
            {
                dialog.SiteQuery = site.Name;
                dialog.SelectedOption = Assert.Single(dialog.SiteResults);
                dialog.DestinationSystem = system;
                dialog.RemainingTimeText = remaining;
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
