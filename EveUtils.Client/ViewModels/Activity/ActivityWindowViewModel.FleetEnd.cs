using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// Following the commander's SAVE or DISCARD of the shared run (ET-458). With the setting on, the member's own run
/// does the same after <see cref="FollowFleetCommanderEnd.Countdown"/> unless they cancel; with it off they only hear
/// about it. Only this window's own runs are ever touched — the ones filed under the commander's group code — and a
/// discard that would throw away loot or corrections the pilot added themselves waits for a yes instead of a clock.
/// </summary>
public sealed partial class ActivityWindowViewModel
{
    private enum FleetEnd
    {
        Save,
        Discard
    }

    private DispatcherTimer? _followTimer;
    private DateTime _followDeadlineUtc;
    private FleetEnd? _followedEnd;
    private bool _followNeedsConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowShown))]
    private string? _followText;

    [ObservableProperty]
    private string _followActionLabel = "";

    [ObservableProperty]
    private string _followCancelLabel = "Cancel";

    public bool IsFollowShown => FollowText is not null;

    private void _AnnounceSaveToFleet(DateTime savedAtUtc)
    {
        if (!Authority.IsFleetCommander || FleetId is not { } fleetId || GroupCode is not { } groupCode
            || _services.GetService<IEventBus>() is not { } eventBus)
            return;

        _ = eventBus.PublishAsync(
            new FleetRunSavedEvent(new RunGroupSave(fleetId, Kind, groupCode, savedAtUtc), _ActingCharacterId()),
            EventTarget.Both);
    }

    private void _OnFleetRunSaved(FleetRunSavedEvent integrationEvent)
    {
        RunGroupSave save = integrationEvent.Data;
        if (Authority.IsFleetCommander || RunId is null || GroupCode is not { } groupCode
            || !string.Equals(groupCode, save.GroupCode, StringComparison.Ordinal)
            || RunState is ActivityRunState.NotStarted or ActivityRunState.Saved or ActivityRunState.Discarded)
            return;

        Dispatcher.UIThread.Post(() => _BeginFollowingFleetCommander(FleetEnd.Save, save.FleetId));
    }

    private void _BeginFollowingFleetCommander(FleetEnd end, long fleetId) => _ = _BeginFollowingAsync(end, fleetId);

    private async Task _BeginFollowingAsync(FleetEnd end, long fleetId)
    {
        try
        {
            bool follows = _services.GetService<ISettingRepository>() is { } settings
                           && await FollowFleetCommanderEnd.IsOnAsync(settings, fleetId);
            if (!follows)
            {
                if (end is FleetEnd.Save)
                    RunNoticeText = "The fleet commander saved this run. Yours is still open: save it when you are ready.";
                return;
            }

            _StartFollowing(end, DateTime.UtcNow);
        }
        catch (Exception exception)
        {
            _services.GetService<ILogger<ActivityWindowViewModel>>()?.LogError(
                exception, "Could not start following the fleet commander's {End}", end);
        }
    }

    private void _StartFollowing(FleetEnd end, DateTime nowUtc)
    {
        if (_followedEnd is not null)
            return;

        _followedEnd = end;
        _followNeedsConfirmation = end is FleetEnd.Discard && _HasOwnRunData();
        _followDeadlineUtc = nowUtc + FollowFleetCommanderEnd.Countdown;
        FollowActionLabel = end is FleetEnd.Save ? "Save now" : "Discard now";
        FollowCancelLabel = _followNeedsConfirmation ? "Keep my run" : "Cancel";
        _RefreshFollowText(nowUtc);

        if (_followNeedsConfirmation)
            return;

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _followTimer.Tick += (_, _) => TickFollowCountdown(DateTime.UtcNow);
        _followTimer.Start();
    }

    /// <summary>Advances the countdown to <paramref name="nowUtc"/> and, once it has run out, does what the commander
    /// did. Public so the clock can be driven without waiting ten real seconds.</summary>
    public void TickFollowCountdown(DateTime nowUtc)
    {
        if (_followedEnd is null || _followNeedsConfirmation)
            return;

        if (nowUtc < _followDeadlineUtc)
        {
            _RefreshFollowText(nowUtc);
            return;
        }

        // Loot added while the clock ran counts the same as loot added before it.
        if (_followedEnd is FleetEnd.Discard && _HasOwnRunData())
        {
            _StopFollowTimer();
            _followNeedsConfirmation = true;
            FollowCancelLabel = "Keep my run";
            _RefreshFollowText(nowUtc);
            return;
        }

        _ = _FollowNowAsync();
    }

    private void _RefreshFollowText(DateTime nowUtc)
    {
        int seconds = Math.Max(0, (int)Math.Ceiling((_followDeadlineUtc - nowUtc).TotalSeconds));
        FollowText = (_followedEnd, _followNeedsConfirmation) switch
        {
            (FleetEnd.Save, _) => $"The FC saved this run — saving in {seconds} s",
            (_, true) => "The FC discarded this run. You added loot or corrections of your own to it, so it is only "
                         + "discarded if you say so.",
            _ => $"The FC discarded this run — discarding in {seconds} s. Everything you added to it yourself, such as "
                 + "loot, is thrown away with it."
        };
    }

    private bool _HasOwnRunData() => IsTimeCorrected || RunLoot is { Captures.Count: > 0 };

    private void _StopFollowTimer()
    {
        _followTimer?.Stop();
        _followTimer = null;
    }

    private void _EndFollowing()
    {
        _StopFollowTimer();
        _followedEnd = null;
        _followNeedsConfirmation = false;
        FollowText = null;
    }

    [RelayCommand]
    private Task FollowNowAsync() => _FollowNowAsync();

    private async Task _FollowNowAsync()
    {
        if (_followedEnd is not { } end)
            return;

        _EndFollowing();
        if (end is FleetEnd.Save)
        {
            await _SaveRunAsync(offerNextRun: false);
            return;
        }

        await _DiscardFollowedRunsAsync();
    }

    /// <summary>Leaves the run open for the pilot to decide about.</summary>
    [RelayCommand]
    private void CancelFollow() => _EndFollowing();

    private async Task _DiscardFollowedRunsAsync()
    {
        List<Guid> runIds = [.. Participants.Select(participant => participant.RunId)];
        if (RunId is { } ownRunId && !runIds.Contains(ownRunId))
            runIds.Add(ownRunId);

        DateTime nowUtc = DateTime.UtcNow;
        using var scope = _services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        foreach (Guid runId in runIds)
        {
            var discarded = await dispatcher.Send(new DiscardRunCommand(runId, nowUtc, DeleteAfterDiscard: true));
            if (!discarded.IsSuccess)
            {
                _services.GetService<IToastService>()?.Show("Run not discarded",
                    discarded.Messages.FirstOrDefault()?.Text ?? "Could not discard this run.", ToastKind.Error);
                return;
            }
        }

        _services.GetService<IToastService>()?.Show("Run discarded", "Your run followed the fleet commander's discard.",
            ToastKind.Success);
        _SendPendingCopyToANewWindow();
        CloseRequested?.Invoke();
    }
}
