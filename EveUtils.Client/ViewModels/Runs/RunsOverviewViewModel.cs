using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-161: the runs screen in the shell — the band of running runs on top, then one row per finished activity,
/// grouped under a day band. Until this existed a saved run left the screen the moment its window closed and there
/// was nowhere in the app to see what you flew yesterday.
///
/// <para><b>The width this is designed at, and why.</b> Jithran's open question — design at the start size or at the
/// window as it actually stands — is answered here as <i>the start size, 758px</i>, and the answer is a choice, not
/// a measurement. Three reasons. <c>ModuleHostService.Render</c> moves this very <c>Content</c> between a docked tab
/// and a floating window, so there is exactly one layout and its binding constraint is the narrowest width it must
/// survive; 758 is that width and it is the default, not an edge case. "The window as it stands" is not one number —
/// docked and floating differ by 342px and no two operators keep the same size — so designing to it is designing to
/// nothing. And elastic layout is free here: star columns, a wrapping chip strip and character-ellipsis trimming
/// cost nothing at 1180 and are the whole of what makes 758 work. Wider is therefore not a second design but the
/// same one with more room, which is also why no element is folded behind a "⋯": an overflow menu would hide, at
/// the default width, exactly the rewards AC-3 says may never disappear.</para>
/// </summary>
public sealed partial class RunsOverviewViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<long, string> _namesById;
    private readonly DispatcherTimer? _clock;

    public RunsOverviewViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, bool runClock = true)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _namesById = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);

        // A lane per local character, running or not — the roster is the band, and today it happens to hold at most
        // one running run because RunningRunLookup answers only when there is exactly one (ET-130 is what lifts that).
        Lanes = [.. characters
            .Where(character => character.EsiCharacterId is > 0)
            .Select(character => new RunningLaneViewModel(character, _ActOnLaneAsync))];
        LanesEmptyText = Lanes.Count == 0
            ? "No character is linked yet, so there is no lane to run one on."
            : null;

        if (!runClock)
            return;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += _OnClockTick;
        _clock.Start();
    }

    public ObservableCollection<RunsDayViewModel> Days { get; } = [];

    public ObservableCollection<RunningLaneViewModel> Lanes { get; }

    /// <summary>Why the band is empty, when it is. Null once there is at least one lane.</summary>
    public string? LanesEmptyText { get; }

    /// <summary>Why nothing is listed — a failed read and an empty history are different things and say so.</summary>
    [ObservableProperty] private string? _statusMessage;

    public void RefreshModule() => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _LoadLanesAsync(cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await _dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        Days.Clear();
        if (!overview.IsSuccess || overview.Value is null)
        {
            StatusMessage = overview.Messages.Count > 0 ? overview.Messages[0].Text : "The activities could not be read.";
            return;
        }

        List<ActivityOverviewRowViewModel> rows = [.. overview.Value.Select(row =>
            new ActivityOverviewRowViewModel(row, _NameOf, _LoadSubRunsAsync, _OpenDetailAsync))];
        foreach (IGrouping<DateTime, ActivityOverviewRowViewModel> day in rows.GroupBy(row => row.StartedAtLocal.Date))
            Days.Add(new RunsDayViewModel(day.Key, [.. day]));

        StatusMessage = Days.Count == 0
            ? "No activity has been saved yet. A run shows up here the moment you save it."
            : null;
    }

    private async Task _LoadLanesAsync(CancellationToken cancellationToken)
    {
        Result<RunningRunDto> running = await _dispatcher.Query(new GetRunningRunQuery(), cancellationToken);
        RunningRunDto? run = running.IsSuccess ? running.Value : null;
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Attach(run is not null && (long?)lane.Character.EsiCharacterId == run.CharacterId ? run : null, nowUtc);
    }

    private void _OnClockTick(object? sender, EventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Tick(nowUtc);
    }

    private string _NameOf(long characterId) =>
        _namesById.TryGetValue(characterId, out string? name) ? name : $"character {characterId}";

    /// <summary>The deelruns behind one row, read through the detail query rather than a read path of this screen's
    /// own — ET-160 owns what an activity's runs are, and a second answer here could disagree with the detail
    /// screen the same row opens.</summary>
    private async Task _LoadSubRunsAsync(ActivityOverviewRowViewModel row)
    {
        Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));
        row.SubRuns.Clear();
        if (!detail.IsSuccess || detail.Value is null)
        {
            row.SubRunsStatus = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The runs could not be read.";
            return;
        }

        row.SubRunsStatus = null;
        foreach (ActivityRunDetailDto run in detail.Value.Runs.OrderBy(run => run.StartedAtUtc))
            row.SubRuns.Add(new ActivityRunRowViewModel(run, _NameOf));
    }

    /// <summary>The row is the only way into ET-162's detail screen; nothing else in the app reaches it. The screen
    /// reads itself once it is routed, so nothing is fetched here.</summary>
    private Task _OpenDetailAsync(ActivityOverviewRowViewModel row)
    {
        _dialogs.ShowActivityDetail(
            new ActivityDetailViewModel(_dispatcher, row.ActivitySummaryId, _services.GetService<IMarketPriceRepository>()),
            row.ActivitySummaryId);
        return Task.CompletedTask;
    }

    private Task _ActOnLaneAsync(RunningLaneViewModel lane)
    {
        if (lane.Run is { } run)
        {
            // The run window adopts the stored running run itself, so it only has to be opened; it is also the one
            // place that owns STOP and SAVE, which is why this lane does not carry a second copy of either.
            // Its own kind knows only two shapes, so the mapping is the one FleetRunWindowPresenter already makes.
            ActivityKind kind = run.ActivityKind == StoredActivityKind.Abyssal ? ActivityKind.Abyssal : ActivityKind.Site;
            _dialogs.ShowActivityWindow(new ActivityWindowViewModel(kind, _services));
        }
        else if (_services.GetService<ISdeAccessor>() is { } sde)
            // Handed only this pilot, so the screen opens on the lane the operator pressed rather than on whoever
            // happens to sort first.
            _dialogs.ShowManualRunStart(new ManualRunStartViewModel(_dispatcher, sde, [lane.Character]));

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }
}
