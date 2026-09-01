using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

namespace EveUtils.Client.Runs;

/// <summary>
/// The fleet commander starts the run and it comes up on every member's screen (ET-105). Deliberately the only
/// caller that passes <see cref="RunWindowOpenTrigger.RemoteFleetCommander"/>: this is the one path where a window
/// appears because somebody else acted, and it must not take the keyboard from a pilot who is mid-fight in EVE.
///
/// The commander's own client receives its own event too, and needs no special case — its window is already up, so
/// the presentation rule answers <see cref="RunWindowActivation.LeaveAsIs"/> and nothing happens.
/// </summary>
public sealed class FleetRunWindowPresenter : ISingletonService, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;

    public FleetRunWindowPresenter(IEventBus eventBus, IDialogService dialogs, IServiceProvider services)
    {
        _dialogs = dialogs;
        _services = services;
        _subscription = eventBus.Subscribe<FleetRunGroupCodeEvent>(_OnFleetRunStartedAsync);
    }

    public void Dispose() => _subscription.Dispose();

    private Task _OnFleetRunStartedAsync(FleetRunGroupCodeEvent integrationEvent, CancellationToken cancellationToken)
    {
        // Only the commander's start opens windows for everybody. A member's own start is their own business.
        if (!integrationEvent.Data.IsFleetCommander)
            return Task.CompletedTask;

        ActivityKind kind = integrationEvent.Data.ActivityKind == StoredActivityKind.Abyssal
            ? ActivityKind.Abyssal
            : ActivityKind.Site;
        string groupCode = integrationEvent.Data.GroupCode;
        long fleetId = integrationEvent.Data.FleetId;

        Dispatcher.UIThread.Post(() => _dialogs.ShowActivityWindow(
            new ActivityWindowViewModel(kind, _services) { GroupCode = groupCode, FleetId = fleetId },
            RunWindowOpenTrigger.RemoteFleetCommander));
        return Task.CompletedTask;
    }
}
