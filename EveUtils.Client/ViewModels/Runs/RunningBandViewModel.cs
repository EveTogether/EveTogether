using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>A running run with its type and system already named — resolved off the UI thread with the read.</summary>
public sealed record RunningRunFacts(RunningRunDto Run, string TypeText, string? SystemText);

/// <summary>
/// RUNNING (ET-290): a lane per local character, running or not. Each asks <see cref="GetRunningRunsQuery"/> (ET-203)
/// which run is running for ITS character, so two characters running at once are two running lanes — that query has no
/// "exactly one" rule to hit, unlike the single-run query a run window uses to reopen (ET-130). The band draws them as
/// a line per running group and an avatar per idle character. The runs overview and the home (ET-324) show the same
/// band; neither reads for it here — each hands over what its own read brought.
/// </summary>
public sealed partial class RunningBandViewModel : ObservableObject
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly Func<long, string, CharacterFaceViewModel> _faceOf;

    public RunningBandViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, Func<long, string, CharacterFaceViewModel> faceOf)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _faceOf = faceOf;
        ShowCharacters(characters);
    }

    /// <summary>Every local character's place in RUNNING, running or not.</summary>
    public ObservableCollection<RunningLaneViewModel> Lanes { get; } = [];

    /// <summary>A line per running group this machine's characters are on (<c>GroupCode ?? RunId</c>), earliest
    /// start first.</summary>
    public ObservableCollection<RunningGroupViewModel> RunningGroups { get; } = [];

    /// <summary>The characters with nothing running — an avatar each, one click from a start fixed on them.</summary>
    public ObservableCollection<RunningLaneViewModel> IdleLanes { get; } = [];

    [ObservableProperty] private bool _hasRunning;

    /// <summary>Why the band has nobody in it, when it has not.</summary>
    [ObservableProperty] private string? _lanesEmptyText;

    /// <summary>What the band says with nothing running; the home adds when the last run ended.</summary>
    [ObservableProperty] private string _idleText = "nothing running";

    /// <summary>Off the UI thread: the running runs with their type and system named, or null when the read failed.</summary>
    public static async Task<IReadOnlyList<RunningRunFacts>?> ReadAsync(CqrsDispatcher dispatcher, RunRowFacts facts)
    {
        Result<IReadOnlyList<RunningRunDto>> running = await dispatcher.Query(new GetRunningRunsQuery());
        if (!running.IsSuccess)
            return null;

        return [.. (running.Value ?? []).Select(run => new RunningRunFacts(run,
            facts.TypeOf(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId, run.SiteName).Name,
            facts.SystemOf(run.SolarSystemId)?.Name))];
    }

    /// <summary>One lane per character with an ESI id; a lane already standing keeps its face and its run.</summary>
    public void ShowCharacters(IReadOnlyList<Character> characters)
    {
        Dictionary<int, RunningLaneViewModel> shown = Lanes.ToDictionary(lane => lane.Character.EsiCharacterId ?? 0);
        List<RunningLaneViewModel> lanes = [];
        foreach (Character character in characters)
        {
            if (character.EsiCharacterId is not (> 0 and var id))
                continue;

            lanes.Add(shown.GetValueOrDefault(id)
                      ?? new RunningLaneViewModel(character, _faceOf(id, character.Name), _ActOnLaneAsync));
        }

        Lanes.ReconcileTo(lanes);
        IdleLanes.ReconcileTo([.. Lanes.Where(lane => !lane.IsRunning)]);
        LanesEmptyText = Lanes.Count == 0
            ? "No character is linked yet, so there is no one to start a run for."
            : null;
    }

    public void Show(IReadOnlyList<RunningRunFacts> running)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
        {
            RunningRunFacts? facts = running.FirstOrDefault(run => (long?)lane.Character.EsiCharacterId == run.Run.CharacterId);
            lane.Attach(facts?.Run, nowUtc, facts?.TypeText ?? string.Empty, facts?.SystemText);
        }

        // Only groups one of this machine's own characters is on: a running row left behind for a character that is
        // not local shows nothing (ET-203).
        Dictionary<string, RunningGroupViewModel> shownGroups = RunningGroups.ToDictionary(group => group.Key);
        List<RunningGroupViewModel> groups = [];
        foreach (IGrouping<string, RunningLaneViewModel> group in Lanes
                     .Where(lane => lane.Run is not null)
                     .GroupBy(lane => lane.Run?.GroupCode ?? lane.Run?.Id.ToString() ?? string.Empty)
                     .OrderBy(group => group.Min(lane => lane.Run?.StartedAtUtc)))
        {
            RunningGroupViewModel line = shownGroups.GetValueOrDefault(group.Key)
                ?? new RunningGroupViewModel(group.Key, _OpenRunningGroupAsync);
            line.Show([.. group], nowUtc);
            groups.Add(line);
        }

        RunningGroups.ReconcileTo(groups);
        for (int index = 0; index < groups.Count; index++)
            groups[index].IsFirst = index == 0;
        IdleLanes.ReconcileTo([.. Lanes.Where(lane => !lane.IsRunning)]);
        HasRunning = groups.Count > 0;
    }

    /// <summary>Text only, never a read: the clocks count from starts the last read already brought.</summary>
    public void Tick(DateTime nowUtc)
    {
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Tick(nowUtc);
        foreach (RunningGroupViewModel group in RunningGroups)
            group.Tick(nowUtc);
    }

    /// <summary>
    /// A character's avatar in RUNNING, or their running line's OPEN. Idle, it is the one-click start (Jithran,
    /// ET-290): the manual start screen with exactly this character in it and no picker. Type and site are chosen
    /// there — the type comes preset from that screen's own memory — because a run needs both, and one started on a
    /// guessed site gets the wrong name. The band never starts a run itself.
    /// </summary>
    private async Task _ActOnLaneAsync(RunningLaneViewModel lane)
    {
        if (lane.Run is { } run)
        {
            _OpenRunWindow(run, lane.CharacterText);
            return;
        }

        if (_services.GetService<ISdeAccessor>() is not { } sde)
            return;

        await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
            kind => new ActivityWindowViewModel(kind, _services), [lane.Character],
            preselectedCharacter: lane.Character, toasts: _services.GetService<IToastService>(),
            fleetParticipation: _services.GetService<IFleetParticipation>(),
            localPresence: _services.GetService<ILocalCharacterPresence>(), isCharacterFixed: true));
    }

    private Task _OpenRunningGroupAsync(RunningGroupViewModel group)
    {
        if (group.Lanes.FirstOrDefault(lane => lane.Run is not null) is { Run: { } run } first)
            _OpenRunWindow(run, first.CharacterText);
        return Task.CompletedTask;
    }

    /// <summary>The run window adopts the stored running run itself, so it only has to be opened; it is also the one
    /// place that owns STOP and SAVE. Named first (ET-221): with two groups running, a window left to find "the one run
    /// running" would find neither.</summary>
    private void _OpenRunWindow(RunningRunDto run, string characterName)
    {
        var window = new ActivityWindowViewModel(run.ActivityKind, _services);
        window.UseCharacter(checked((int)run.CharacterId), characterName);
        _dialogs.ShowActivityWindow(window);
    }

    /// <summary>START RUN ▾: the general start, the same as Tools → Start run — every character offered (ET-221), none
    /// preselected, so the screen's own default applies: the fleet, or the team last picked (ET-270).</summary>
    [RelayCommand]
    private async Task OpenRunStartAsync()
    {
        if (_services.GetService<ISdeAccessor>() is not { } sde)
            return;

        ICharacterRegistry registry = _services.GetRequiredService<ICharacterRegistry>();
        IReadOnlyList<Character> characters = await Task.Run(() => registry.GetAllAsync());
        await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
            kind => new ActivityWindowViewModel(kind, _services), characters,
            toasts: _services.GetService<IToastService>(),
            fleetParticipation: _services.GetService<IFleetParticipation>(),
            localPresence: _services.GetService<ILocalCharacterPresence>()));
    }
}
