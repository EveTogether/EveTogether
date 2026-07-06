using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Modules.Messaging.Repositories;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.Modules.Messaging.Commands;

internal sealed class EnqueueMessageCommandHandler(IMessageRepository repository, IRuntimeContext runtime, IEventBus eventBus)
    : ICommandHandler<EnqueueMessageCommand, Result<long>>
{
    /// <summary>Server retention cap: a message lives at most ~30 days before the cleanup sweep removes it.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<Result<long>> Handle(EnqueueMessageCommand command, CancellationToken cancellationToken = default)
    {
        // The message queue is server-only: QueuedMessage lives in ServerDbContext, not ClientDbContext.
        // On the client there is no inbox to enqueue to (e.g. a local-only fleet notifying its members), so this is a
        // no-op success rather than an error — without it the shared fleet handlers crash with "Cannot create a DbSet
        // for 'QueuedMessage'" the moment a local-only fleet with non-owner members is started or concluded.
        if (runtime.Host == ExecutionHost.Client)
            return Result<long>.Success(0);

        if (command.RecipientCharacterId <= 0)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A recipient character is required.", "Messaging"));

        if (string.IsNullOrWhiteSpace(command.Title))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A message title is required.", "Messaging"));

        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = await repository.AddAsync(new QueuedMessage
            {
                RecipientCharacterId = command.RecipientCharacterId,
                SenderCharacterId = command.SenderCharacterId,
                Kind = command.Kind,
                RefId = command.RefId,
                Title = command.Title,
                Body = command.Body,
                PayloadJson = command.PayloadJson,
                CreatedAt = now,
                ExpiresAt = now + Retention,
                Status = MessageStatus.Pending
            }, cancellationToken);

            // Live-delivery trigger: raise on every enqueue path so the server pushes the recipient's pending
            // messages to their open connections at once — no call site has to remember it. Local-only (the client
            // returned above). The durable queue + on-connect sweep stay the offline fallback.
            await eventBus.PublishAsync(
                new MessageEnqueuedEvent(command.RecipientCharacterId), EventTarget.Local, cancellationToken);

            return Result<long>.Success(id);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ServerError, $"Failed to enqueue message: {ex.Message}", "Messaging"));
        }
    }
}
