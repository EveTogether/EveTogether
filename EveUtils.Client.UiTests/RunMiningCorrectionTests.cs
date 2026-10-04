using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-424: the mining lines of a run can be corrected the way loot is — the units of a line set to another figure, a
/// line taken off — on a saved activity and on a running one, through the same lock, summary rebuild and
/// "changed since published" mark as a loot correction. The three tests on the saved activity are the counter-proofs;
/// each was shown red with the commands taking nothing off (the figures stayed at what the gamelog recorded).
/// </summary>
public sealed class RunMiningCorrectionTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string GroupCode = "HF-MC42";
    private const string ServerAddress = "https://alpha.invalid";
    private const string Veldspar = "Veldspar";
    private const string Scordite = "Scordite";

    private static readonly IReadOnlyList<Character> Crew = [new("Jithran", 90000001), new("Abnoba Auscent", 90000002)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Setting a line to a lower figure on a saved activity moves what is made of it: the line, the character's total,
    /// TOTAL ISK at the top and the day on the runs screen. Crit is already inside the units and residue was never part
    /// of them, so crit stays below the new figure and residue stays as recorded.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingALineLower_OnASavedActivity_MovesTheLineTotalIskAndTheDay()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceOresAsync(instance);
        Guid runId = await _SaveMiningRunAsync(dispatcher, 90000001, groupCode: null,
            (Veldspar, 1000, 100, 50), (Scordite, 400, 0, 0));
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        ActivityDetailViewModel detail = await _DetailAsync(instance, overview);
        Assert.Equal($"{1_400m:N0} ISK", detail.TotalIskText);

        ActivityMiningRowViewModel row = _Line(detail, Veldspar);
        Assert.True(row.CanEdit);
        row.BeginEditCommand.Execute(null);
        row.EditText = "300";
        await row.SaveEditCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{700m:N0} ISK" && _Lines(detail).Any(line => line is { OreText: Veldspar, UnitsText: "300" })
                                                    && overview.Tabs[0].Days.Single().SummaryText.Contains("+700 ISK net"));

        Assert.Equal("300", _Line(detail, Veldspar).UnitsText);
        Assert.Equal("400", _Line(detail, Scordite).UnitsText);
        Assert.Equal($"{700m:N0} ISK", detail.TotalIskText);
        Assert.Contains("+700 ISK net", overview.Tabs[0].Days.Single().SummaryText);
        Assert.Null(detail.StatusMessage);
        RunMiningEntry stored = Assert.Single(await _EntriesAsync(instance, runId), entry => entry.OreType == Veldspar);
        Assert.Equal(300, stored.Units);
        Assert.Equal(100, stored.CriticalUnits);
        Assert.Equal(50, stored.ResidueUnits);
    }

    /// <summary>
    /// Taking a line off a saved activity takes its crit and residue with it, and every figure made of it. This is the
    /// case that started the ticket: ore another character's gamelog put on a run that was never theirs.
    /// </summary>
    [AvaloniaFact]
    public async Task RemovingALine_OnASavedActivity_TakesItsCritResidueAndIskWithIt()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceOresAsync(instance);
        Guid runId = await _SaveMiningRunAsync(dispatcher, 90000001, groupCode: null,
            (Veldspar, 1000, 100, 50), (Scordite, 400, 0, 0));
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        ActivityDetailViewModel detail = await _DetailAsync(instance, overview);

        ActivityMiningRowViewModel row = _Line(detail, Veldspar);
        row.BeginEditCommand.Execute(null);
        await row.RemoveLineCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => detail.TotalIskText == $"{400m:N0} ISK" && _Lines(detail).Count == 1
                                                    && overview.Tabs[0].Days.Single().SummaryText.Contains("+400 ISK net"));

        ActivityMiningRowViewModel remaining = Assert.Single(detail.Mining().Groups.Single().Ores);
        Assert.Equal(Scordite, remaining.OreText);
        Assert.Equal($"{400m:N0} ISK", detail.TotalIskText);
        RunMiningEntry stored = Assert.Single(await _EntriesAsync(instance, runId));
        Assert.Equal(Scordite, stored.OreType);
    }

    /// <summary>
    /// A correction on a published run says the server's copy is behind and pushes nothing by itself: the run turns
    /// Outdated, never Pending, and its revision moves — the same as a loot correction. The correction goes out with
    /// the run when the pilot publishes again, because the wire copy is built from the stored lines.
    /// </summary>
    [AvaloniaFact]
    public async Task CorrectingAPublishedRunsMining_MarksItChangedSincePublished_AndTheWireCopyCarriesIt()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceOresAsync(instance);
        Guid runId = await _SaveMiningRunAsync(dispatcher, 90000001, groupCode: null, (Veldspar, 1000, 100, 50));
        await _MarkPublishedAsync(instance, runId);
        int revisionBefore = (await _RunAsync(instance, runId)).Revision;
        RunsOverviewViewModel overview = await _OverviewAsync(instance);
        int republished = 0;
        ActivityDetailViewModel detail = await _DetailAsync(instance, overview, () =>
        {
            republished++;
            return Task.CompletedTask;
        });
        Assert.False(detail.IsPublishedCopyBehind);

        ActivityMiningRowViewModel row = _Line(detail, Veldspar);
        row.BeginEditCommand.Execute(null);
        row.EditText = "300";
        await row.SaveEditCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => detail.IsPublishedCopyBehind);

        Assert.True(detail.CanRepublish);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal(RunSyncState.Outdated, run.SyncState);
        Assert.Equal(revisionBefore + 1, run.Revision);
        Assert.Equal(0, republished);
        RunMiningEntryInput onTheWire = Assert.Single(RunWireData.FromEntity(await _RunWithMiningAsync(instance, runId)).MiningEntries);
        Assert.Equal(300, onTheWire.Units);
    }

    /// <summary>
    /// A running run takes the same corrections, and there is no summary to rebuild and nothing to mark: the run was
    /// never saved or published. Counter-proof: let the commands treat a running run like a saved one and a rebuild is
    /// sent for an activity that has no summary yet.
    /// </summary>
    [AvaloniaFact]
    public async Task ARunningRun_TakesBothCorrections_WithoutRebuildingOrMarkingAnything()
    {
        List<RebuildActivitySummariesCommand> rebuilds = [];
        using var instance = _Instance(services => services.Decorate<ICommandHandler<RebuildActivitySummariesCommand, Result<int>>>(
            (inner, _) => new RecordingRebuildHandler(inner, rebuilds)));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartMiningRunAsync(dispatcher, 90000001, groupCode: null, (Veldspar, 1000, 100, 50), (Scordite, 400, 0, 0));
        Run before = await _RunAsync(instance, runId);

        Assert.True((await dispatcher.Send(new SetRunMiningEntryUnitsCommand(runId, Veldspar, 300), Token)).IsSuccess);
        Assert.True((await dispatcher.Send(new RemoveRunMiningEntryCommand(runId, Scordite), Token)).IsSuccess);

        RunMiningEntry stored = Assert.Single(await _EntriesAsync(instance, runId));
        Assert.Equal((Veldspar, 300, 100, 50), (stored.OreType, stored.Units, stored.CriticalUnits, stored.ResidueUnits));
        Assert.Empty(rebuilds);
        Run after = await _RunAsync(instance, runId);
        Assert.Equal(RunState.Running, after.State);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.SyncState, after.SyncState);
    }

    /// <summary>The run window's own MINING section: its lines carry the edit action for this machine's own pilot, and
    /// a correction shows on the next tick like any other mining that came in.</summary>
    [AvaloniaFact]
    public async Task TheRunWindowsMiningLines_CanBeSetAndRemoved_WhileTheRunIsRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureGroup = "Combat Site";
        model.SignatureId = "AAA-001";
        model.SignatureName = "Sansha Hideaway";
        await model.StartRunCommand.ExecuteAsync(null);
        IDispatcher dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new AddRunMiningEntryCommand(ActivityWindowHarness.CharacterId, DateTime.UtcNow, Veldspar, 13, false, 0), Token);
        await dispatcher.Send(new AddRunMiningEntryCommand(ActivityWindowHarness.CharacterId, DateTime.UtcNow, Scordite, 8, false, 0), Token);
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.Sections.OfType<MiningWindowSectionViewModel>().SingleOrDefault()
                is { } section && _WindowLines(section).Count == 2 && _WindowLines(section).All(line => line.CanEdit);
        });
        MiningWindowSectionViewModel mining = model.Mining();

        ActivityMiningRowViewModel veldspar = _WindowLines(mining).Single(line => line.OreText == Veldspar);
        veldspar.BeginEditCommand.Execute(null);
        veldspar.EditText = "5";
        await veldspar.SaveEditCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return _WindowLines(mining).Any(line => line is { OreText: Veldspar, UnitsText: "5" });
        });

        ActivityMiningRowViewModel scordite = _WindowLines(mining).Single(line => line.OreText == Scordite);
        scordite.BeginEditCommand.Execute(null);
        await scordite.RemoveLineCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return _WindowLines(mining).All(line => line.OreText != Scordite);
        });

        Assert.Equal(Veldspar, Assert.Single(_WindowLines(mining)).OreText);
    }

    /// <summary>
    /// Only this machine's own pilots' lines are correctable (ET-215's rule for loot): anyone else's run came in from a
    /// server and could never be published back. Counter-proof: give every line the action and the correction lands on
    /// a run this pilot does not own.
    /// </summary>
    [AvaloniaFact]
    public async Task SomeoneElsesMiningLine_HasNoEditAction()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceOresAsync(instance);
        await _SaveMiningRunAsync(dispatcher, 90000001, GroupCode, (Veldspar, 1000, 0, 0));
        await _SaveMiningRunAsync(dispatcher, 90000009, GroupCode, (Veldspar, 500, 0, 0));
        ActivityDetailViewModel detail = await _DetailAsync(instance, await _OverviewAsync(instance));

        MiningCharacterGroupViewModel own = Assert.Single(detail.Mining().Groups, group => group.CharacterId == 90000001);
        MiningCharacterGroupViewModel theirs = Assert.Single(detail.Mining().Groups, group => group.CharacterId == 90000009);
        Assert.True(Assert.Single(own.Ores).CanEdit);
        Assert.False(Assert.Single(theirs.Ores).CanEdit);
    }

    /// <summary>
    /// What the commands refuse, each leaving the line as it was: nothing at zero or below (a line is taken off with
    /// REMOVE, not by typing 0), nothing on an ore the run has no line for, nothing on a run that is gone. Crit above
    /// the new figure comes down to it, since crit is counted inside the units.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCommands_RefuseWhatCannotBeCorrected_AndKeepCritWithinTheUnits()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartMiningRunAsync(dispatcher, 90000001, groupCode: null, (Veldspar, 1000, 600, 0));

        Assert.False((await dispatcher.Send(new SetRunMiningEntryUnitsCommand(runId, Veldspar, 0), Token)).IsSuccess);
        Result unknownOre = await dispatcher.Send(new SetRunMiningEntryUnitsCommand(runId, Scordite, 5), Token);
        Assert.False(unknownOre.IsSuccess);
        Assert.Equal(MessageCodes.NotFound, unknownOre.Messages[0].Code);
        Assert.False((await dispatcher.Send(new RemoveRunMiningEntryCommand(runId, Scordite), Token)).IsSuccess);
        Assert.False((await dispatcher.Send(new SetRunMiningEntryUnitsCommand(Guid.NewGuid(), Veldspar, 5), Token)).IsSuccess);
        Assert.Equal(1000, Assert.Single(await _EntriesAsync(instance, runId)).Units);

        Assert.True((await dispatcher.Send(new SetRunMiningEntryUnitsCommand(runId, Veldspar, 200), Token)).IsSuccess);

        RunMiningEntry stored = Assert.Single(await _EntriesAsync(instance, runId));
        Assert.Equal((200, 200), (stored.Units, stored.CriticalUnits));
    }

    /// <summary>Text that is not a whole number of units is turned down beside the box it was typed in, and nothing is
    /// sent; the line stays open for the pilot to fix.</summary>
    [AvaloniaFact]
    public async Task AnUnreadableFigure_IsRefusedInTheBox_AndNothingIsStored()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceOresAsync(instance);
        Guid runId = await _SaveMiningRunAsync(dispatcher, 90000001, groupCode: null, (Veldspar, 1000, 0, 0));
        ActivityDetailViewModel detail = await _DetailAsync(instance, await _OverviewAsync(instance));

        ActivityMiningRowViewModel row = _Line(detail, Veldspar);
        row.BeginEditCommand.Execute(null);
        row.EditText = "a lot";
        await row.SaveEditCommand.ExecuteAsync(null);

        Assert.True(row.IsEditing);
        Assert.NotNull(row.EditError);
        Assert.Equal(1000, Assert.Single(await _EntriesAsync(instance, runId)).Units);
    }

    private sealed class RecordingRebuildHandler(
        ICommandHandler<RebuildActivitySummariesCommand, Result<int>> inner, List<RebuildActivitySummariesCommand> rebuilds)
        : ICommandHandler<RebuildActivitySummariesCommand, Result<int>>
    {
        public Task<Result<int>> Handle(RebuildActivitySummariesCommand command, CancellationToken cancellationToken = default)
        {
            rebuilds.Add(command);
            return inner.Handle(command, cancellationToken);
        }
    }

    private static TestClientInstance _Instance(Action<IServiceCollection>? configure = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(1230, Veldspar, 462, 25)
                .Add(1228, Scordite, 462, 25));
            configure?.Invoke(services);
        });

    private static Task _PriceOresAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 1230, AveragePrice = 1, AdjustedPrice = 1, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 1228, AveragePrice = 1, AdjustedPrice = 1, UpdatedAt = DateTimeOffset.UtcNow }
        ], Token);

    private static async Task<Guid> _StartMiningRunAsync(IDispatcher dispatcher, long characterId, string? groupCode,
        params (string Ore, int Units, int Crit, int Residue)[] lines)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142, groupCode), Token);
        Assert.True(started.IsSuccess);
        foreach ((string ore, int units, int crit, int residue) in lines)
        {
            // Crit units are the part of the line mined by a critical cycle; the residue rides on the last cycle.
            if (units > crit)
                Assert.True((await dispatcher.Send(new AddRunMiningEntryCommand(characterId, StartedAtUtc.AddMinutes(1), ore,
                    units - crit, false, crit > 0 ? 0 : residue), Token)).IsSuccess);
            if (crit > 0)
                Assert.True((await dispatcher.Send(new AddRunMiningEntryCommand(characterId, StartedAtUtc.AddMinutes(2), ore,
                    crit, true, residue), Token)).IsSuccess);
        }

        return started.Value;
    }

    private static async Task<Guid> _SaveMiningRunAsync(IDispatcher dispatcher, long characterId, string? groupCode,
        params (string Ore, int Units, int Crit, int Residue)[] lines)
    {
        Guid runId = await _StartMiningRunAsync(dispatcher, characterId, groupCode, lines);
        Assert.True((await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), Token)).IsSuccess);
        return runId;
    }

    private static async Task<RunsOverviewViewModel> _OverviewAsync(TestClientInstance instance)
    {
        var overview = new RunsOverviewViewModel(instance.Services.GetRequiredService<IDispatcher>(),
            new RecordingDialogService(), instance.Services, Crew, runClock: false, time: RunsTestClock.Fixed);
        await overview.LoadAsync(Token);
        return overview;
    }

    private static async Task<ActivityDetailViewModel> _DetailAsync(TestClientInstance instance, RunsOverviewViewModel overview,
        Func<Task>? republish = null)
    {
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);
        var detail = new ActivityDetailViewModel(instance.Services.GetRequiredService<IDispatcher>(), row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            id => Crew.FirstOrDefault(character => character.EsiCharacterId == id)?.Name ?? $"character {id}",
            sde: instance.Services.GetRequiredService<ISdeAccessor>(),
            ownCharacterIds: Crew.Select(character => (long)character.EsiCharacterId.GetValueOrDefault()).ToHashSet(),
            republish: republish);
        await detail.LoadAsync(Token);
        return detail;
    }

    private static List<ActivityMiningRowViewModel> _Lines(ActivityDetailViewModel detail) =>
        [.. detail.Mining().Groups.SelectMany(group => group.Ores)];

    private static ActivityMiningRowViewModel _Line(ActivityDetailViewModel detail, string ore) =>
        Assert.Single(detail.Mining().Groups.SelectMany(group => group.Ores), line => line.OreText == ore);

    private static List<ActivityMiningRowViewModel> _WindowLines(MiningWindowSectionViewModel section) =>
        [.. section.Groups.SelectMany(group => group.Ores)];

    private static async Task<List<RunMiningEntry>> _EntriesAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        return await db.Set<RunMiningEntry>().AsNoTracking().Where(entry => entry.RunId == runId).ToListAsync(Token);
    }

    private static async Task _MarkPublishedAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        await db.Set<Run>().Where(run => run.Id == runId).ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.SyncState, RunSyncState.Synced)
            .SetProperty(run => run.SyncServerAddress, ServerAddress)
            .SetProperty(run => run.LastPushedAtUtc, DateTime.UtcNow), Token);
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Token);
    }

    private static async Task<Run> _RunWithMiningAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        return await db.Set<Run>().AsNoTracking()
            .Include(run => run.MiningEntries)
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .Include(run => run.Parameters)
            .SingleAsync(run => run.Id == runId, Token);
    }
}
