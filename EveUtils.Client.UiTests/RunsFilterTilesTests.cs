using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-293 (RO-4): the TYPES/CHARACTERS filter tiles. One counter-proof per behaviour the ticket calls out
/// as needing a test — everything on by default meaning no filter, SHOW ALL appearing only once something is off,
/// and a filtered-out activity taking the selection and the drawer with it.</summary>
public sealed class RunsFilterTilesTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Ra Vinter", 90000001), new("Kav Orn", 90000002)
    ];

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) => instance.Services.GetRequiredService<ICqrsDispatcher>();

    private static async Task _SaveAsync(ICqrsDispatcher dispatcher, long characterId, ActivityKind kind,
        CancellationToken cancellationToken, TimeSpan offset = default, string? groupCode = null)
    {
        DateTime startedAt = StartedAtUtc + offset;
        var started = await dispatcher.Send(
            new StartRunCommand(characterId, kind, startedAt, 0, "Sanctum", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAt.AddMinutes(15), startedAt.AddMinutes(16),
            [], [], [], []), cancellationToken);
    }

    private static async Task<RunsOverviewViewModel> _OpenAsync(TestClientInstance instance, CancellationToken cancellationToken,
        IReadOnlyList<Character>? characters = null)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            characters ?? Crew, runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    /// <summary>Counter-proof: a character filter that treated "everything present is selected" the same as "no
    /// filter" would hide this activity the moment it loads, since none of its crew is an own character — exactly
    /// the server-tab case the ticket names. With nothing excluded it must still show.</summary>
    [AvaloniaFact]
    public async Task AllOn_MeansNoFilterAtAll_AndAGroupMateActivityWithNoOwnCharacterStillShows()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Neither character is in Crew below, so this activity carries no own character at all.
        await _SaveAsync(_Dispatcher(instance), 90099999, ActivityKind.Mining, cancellationToken);

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);

        Assert.All(viewModel.TypeFilter.Tiles, tile => Assert.True(tile.IsOn));
        Assert.All(viewModel.CharacterFilter.Tiles, tile => Assert.True(tile.IsOn));
        Assert.Null(viewModel.TypeFilter.SummaryText);
        Assert.Null(viewModel.CharacterFilter.SummaryText);
        Assert.Single(viewModel.Tabs[0].Days.Single().Rows);
    }

    /// <summary>Counter-proof: a summary that always renders "n of m" would show one even at rest — this checks the
    /// header is silent until a tile actually goes off, and quiet again once SHOW ALL brings it back.</summary>
    [AvaloniaFact]
    public async Task ShowAll_AppearsOnlyOnceSomethingIsOff_AndClearsIt()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveAsync(_Dispatcher(instance), 90000001, ActivityKind.Mining, cancellationToken);
        await _SaveAsync(_Dispatcher(instance), 90000001, ActivityKind.Abyssal, cancellationToken, TimeSpan.FromHours(1));

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);
        Assert.Null(viewModel.TypeFilter.SummaryText);

        RunFilterTileViewModel abyssal = viewModel.TypeFilter.Tiles.Single(tile => tile.Name == "Abyssal");
        abyssal.Toggle();

        Assert.Equal("1 of 2", viewModel.TypeFilter.SummaryText);
        Assert.False(abyssal.IsOn);

        viewModel.TypeFilter.ShowAllCommand.Execute(null);

        Assert.Null(viewModel.TypeFilter.SummaryText);
        Assert.All(viewModel.TypeFilter.Tiles, tile => Assert.True(tile.IsOn));
    }

    /// <summary>Counter-proof: without this, a tile toggle would leave the pane showing a run the list no longer
    /// offers — the exact bug <c>_SettleSelection</c> exists to prevent for a folded day, now exercised for a
    /// filtered-out activity instead. An activity merely inside a still-passing type must keep its selection.</summary>
    [AvaloniaFact]
    public async Task TogglingOutTheSelectedActivitysType_DropsTheSelectionAndCloses()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveAsync(_Dispatcher(instance), 90000001, ActivityKind.Mining, cancellationToken);
        await _SaveAsync(_Dispatcher(instance), 90000001, ActivityKind.Abyssal, cancellationToken, TimeSpan.FromHours(1));

        RunsOverviewViewModel viewModel = await _OpenAsync(instance, cancellationToken);
        ActivityOverviewRowViewModel abyssalRow = viewModel.Tabs[0].Days.Single().Rows.Single(row => row.TypeText == "ABYSSAL");
        viewModel.Select(abyssalRow);
        Assert.Same(abyssalRow, viewModel.SelectedRow);

        viewModel.TypeFilter.Tiles.Single(tile => tile.Name == "Abyssal").Toggle();

        Assert.Null(viewModel.SelectedRow);
        Assert.False(viewModel.IsDrawerOpen);
        // The other activity, whose type is still on, keeps its place in the list.
        Assert.Single(viewModel.Tabs[0].Days.Single().Rows);
    }
}
