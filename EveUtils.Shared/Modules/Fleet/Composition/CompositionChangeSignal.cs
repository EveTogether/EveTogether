using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// Publishes the composition signal once a composition handler's write is done (ET-381), on the local bus only — the
/// server's relay decides who else hears it. The same handlers serve a client's local library and a server's shared
/// one, so whether the change is client-only is the host's fact, not the command's.
/// </summary>
public sealed class CompositionChangeSignal(IEventBus eventBus, IRuntimeContext runtime) : IScopedService
{
    /// <param name="isClientOnly">A create that asked to stay local; every other change takes it from the host.</param>
    public Task PublishAsync(
        long compositionId, CompositionChangeKind kind, CancellationToken cancellationToken, bool isClientOnly = false) =>
        eventBus.PublishAsync(
            new CompositionChangedEvent(new CompositionChangePayload(
                compositionId, kind, IsClientOnly: isClientOnly || runtime.Host == ExecutionHost.Client)),
            EventTarget.Local, cancellationToken);
}
