using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// Joining the commander's run later than its start (ET-440). The offer only ever went out at the start, so a member
/// who joined the fleet afterwards, declined it, or was cut off by a discard had no way in: their run went on alone,
/// sharing nothing, without saying so.
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    private DateTime? _runningAnnouncedAtUtc;

    /// <summary>The commander's run this window could join: same fleet, same kind, not the group it is in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJoinFleetRunShown), nameof(JoinFleetRunText))]
    private RunGroupCodeStart? _joinableFleetRun;

    public bool IsJoinFleetRunShown => JoinableFleetRun is not null;

    /// <summary>What the join button stands under: whose run, since when, and what joining does to this one.</summary>
    public string? JoinFleetRunText => JoinableFleetRun is not { } start
        ? null
        : RunId is null || RunState is ActivityRunState.NotStarted
            ? $"{(Authority.FleetCommanderName is { Length: > 0 } commander ? commander : "The fleet commander")}'s run is going since {_LocalTime(start.StartedAtUtc)}. Join it to share your figures with the fleet."
            : $"This run is not part of {(Authority.FleetCommanderName is { Length: > 0 } boss ? boss : "the fleet commander")}'s run, going since {_LocalTime(start.StartedAtUtc)}, so nothing of it reaches the fleet. Join to add it, or start a new run that joins.";

    /// <summary>The commander's own window says, every <see cref="RunningFleetRuns.HeartbeatInterval"/>, that the run is
    /// still going — the one thing a member who missed the start can join on.</summary>
    private void _AnnounceRunningToFleet(DateTime nowUtc)
    {
        if (RunState is not ActivityRunState.Running || !Authority.IsFleetCommander
            || FleetId is not { } fleetId || GroupCode is not { } groupCode || EffectiveStartUtc is not { } startedAtUtc
            || (_runningAnnouncedAtUtc is { } last && nowUtc - last < RunningFleetRuns.HeartbeatInterval)
            || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _runningAnnouncedAtUtc = nowUtc;
        _ = eventBus.PublishAsync(new FleetRunRunningEvent(
            new RunGroupCodeStart(fleetId, Kind, groupCode, startedAtUtc, IsFleetCommander: true,
                SiteName: SignatureName, SolarSystemName: SolarSystem, Signature: SignatureId,
                SignatureGroupSnapshot: SignatureGroup, AbyssalTierIndex: TierIndex, AbyssalWeatherName: Weather?.Name,
                SiteTypeId: _runSiteTypeId ?? 0),
            _ActingCharacterId()), EventTarget.Both);
    }

    private void _RefreshJoinableFleetRun(DateTime nowUtc)
    {
        JoinableFleetRun = RunState is ActivityRunState.Saved || _isDiscarding || FleetId is not { } fleetId
                           || _services.GetService<RunningFleetRuns>() is not { } running
            ? null
            : running.Of(fleetId, new DateTimeOffset(nowUtc, TimeSpan.Zero))
                .FirstOrDefault(start => start.ActivityKind == Kind && start.GroupCode != GroupCode);
    }

    /// <summary>
    /// Join the commander's run. A window with no run of its own joins the way the offer does. A window already on a
    /// run asks: add this run to the fleet's — its figures from before count, because a run is one row and cutting it
    /// at the join would throw away what was mined at the same rock — or stop it here and start a new one that joins.
    /// </summary>
    [RelayCommand]
    private async Task JoinRunningFleetRunAsync()
    {
        if (JoinableFleetRun is not { } start)
            return;

        if (RunId is not { } runId || RunState is ActivityRunState.NotStarted)
        {
            JoinFleetRun(start);
            return;
        }

        bool? addThisRun = await _services.GetRequiredService<IDialogService>().ChooseAsync("Join the fleet run?",
            "Add this run to the fleet's run: everything on it so far is shared and counts for the group. Or stop it " +
            "here — it waits under unfinished — and start a new run that joins.",
            "Add this run", "Start a new run", this);
        if (addThisRun is null)
            return;

        DateTime nowUtc = DateTime.UtcNow;
        if (addThisRun is false)
        {
            if (RunState is ActivityRunState.Running)
                StopRun(nowUtc);
            CloseRequested?.Invoke();
            if (_services.GetService<FleetRunWindowPresenter>() is { } presenter)
                await presenter.JoinAsync(start);
            return;
        }

        using IServiceScope scope = _services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        if (GroupCode is not null)
            await dispatcher.Send(new UnlinkRunFromGroupCodeCommand(runId));
        var linked = await dispatcher.Send(new LinkRunToGroupCodeCommand(runId, start.GroupCode, start.FleetId));
        if (!linked.IsSuccess)
        {
            _services.GetService<IToastService>()?.Show("Not joined",
                linked.Messages.FirstOrDefault()?.Text ?? "This run could not be added to the fleet run.", ToastKind.Error);
            return;
        }

        GroupCode = start.GroupCode;
        FleetId = start.FleetId;
        RunNoticeText = null;
        JoinableFleetRun = null;
        if (RunState is not ActivityRunState.Running)
            await _ResumeAdoptedRunAsync(nowUtc);
        Refresh(nowUtc);
    }
}
