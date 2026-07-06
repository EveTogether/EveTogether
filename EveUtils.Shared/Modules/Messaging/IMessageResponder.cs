using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Messaging;

/// <summary>
/// Open-for-extension seam for message kinds that carry an Accept/Decline response. A higher-level
/// module (e.g. Fleet) registers a responder for its <see cref="MessageKind"/>; the generic
/// RespondToMessage handler resolves the matching one and delegates the domain action to it. This keeps
/// Messaging a fundament-subsystem with no backward dependency on the features that ride on it. Mail carries
/// no responder.
/// </summary>
public interface IMessageResponder
{
    MessageKind Kind { get; }

    Task<Result> RespondAsync(QueuedMessage message, bool accept, int actingCharacterId, CancellationToken cancellationToken = default);
}
