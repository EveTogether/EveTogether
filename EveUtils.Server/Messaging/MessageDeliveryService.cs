using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Modules.Messaging.Queries;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Grpc.Core;

namespace EveUtils.Server.Messaging;

/// <summary>
/// Delivers a character's pending queued messages, applying per-kind retention. Two sinks share the
/// same logic: <see cref="DeliverPendingAsync"/> writes to a single attach stream (deliver-on-connect, the
/// offline fallback) and <see cref="DeliverLiveAsync"/> pushes to a character's open connections the moment a
/// message is enqueued (live, when online). A response-carrying kind — one with a registered
/// <see cref="IMessageResponder"/> — is marked <see cref="MessageStatus.Delivered"/> once pushed, so it is
/// delivered exactly once (still answerable, cleaned up by the expiry sweep); a fire-and-forget kind (mail) is
/// dropped from the queue once delivered.
/// </summary>
public sealed class MessageDeliveryService(
    IDispatcher dispatcher,
    IMessageRepository repository,
    ConnectedClients connectedClients,
    IEnumerable<IMessageResponder> responders) : IScopedService
{
    /// <summary>Pushes a freshly attached character their pending messages over their attach stream (offline fallback).</summary>
    public async Task<int> DeliverPendingAsync(
        IServerStreamWriter<ServerEnvelope> responseStream, int characterId, CancellationToken cancellationToken = default)
    {
        var pending = await dispatcher.Query(new ListPendingMessagesQuery(characterId), cancellationToken);
        if (pending.Count == 0)
            return 0;

        var keep = ResponderKinds();
        var delivered = 0;
        foreach (var message in pending)
        {
            await responseStream.WriteAsync(new ServerEnvelope { Event = ToEnvelope(message) }, cancellationToken);
            delivered++;
            await ApplyRetentionAsync(message, keep, cancellationToken);
        }

        return delivered;
    }

    /// <summary>Pushes a character's pending messages to their open connections the moment they are enqueued
    /// . No-op if the character is offline — the on-connect sweep then handles it.</summary>
    public async Task<int> DeliverLiveAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (!connectedClients.IsConnected(characterId))
            return 0;

        var pending = await dispatcher.Query(new ListPendingMessagesQuery(characterId), cancellationToken);
        if (pending.Count == 0)
            return 0;

        var keep = ResponderKinds();
        var delivered = 0;
        foreach (var message in pending)
        {
            await connectedClients.SendToCharacterAsync(characterId, ToEnvelope(message), cancellationToken);
            delivered++;
            await ApplyRetentionAsync(message, keep, cancellationToken);
        }

        return delivered;
    }

    // Post-delivery retention. A fire-and-forget kind (mail, fleet-start) is dropped from the queue once pushed. An
    // answerable kind (invite / join request) is marked Delivered: it is delivered exactly once and is no longer
    // re-pushed on every reconnect — it stays answerable (RespondTo reads it by id) and the expiry sweep cleans it up
    // if abandoned. This is what stops a still-open invite reappearing after the client cleared its inbox.
    private async Task ApplyRetentionAsync(QueuedMessage message, HashSet<MessageKind> keep, CancellationToken cancellationToken)
    {
        if (keep.Contains(message.Kind))
        {
            message.Status = MessageStatus.Delivered;
            await repository.UpdateAsync(message, cancellationToken);
        }
        else
        {
            await repository.DeleteAsync(message.Id, cancellationToken);
        }
    }

    private HashSet<MessageKind> ResponderKinds() => responders.Select(r => r.Kind).ToHashSet();

    private static EventEnvelope ToEnvelope(QueuedMessage message) =>
        WireEnvelopeFactory.ToEnvelope(new MessageDeliveredEvent(
            new MessageDeliveredPayload(message.Id, message.RecipientCharacterId, message.SenderCharacterId,
                message.Kind, message.RefId, message.Title, message.Body, message.PayloadJson, message.CreatedAt),
            message.SenderCharacterId));
}
