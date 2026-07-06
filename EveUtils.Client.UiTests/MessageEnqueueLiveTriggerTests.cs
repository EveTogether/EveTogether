using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Modules.Messaging.Repositories;
using EveUtils.Shared.Runtime;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// enqueuing a message must raise <see cref="MessageEnqueuedEvent"/> on the local bus so the server can
/// live-deliver it centrally — without this trigger a "fleet started" / conclude / responder notice only reaches an
/// already-connected member on their next reconnect (the on-connect sweep). Guards the central enqueue seam so the
/// live-delivery path can't silently regress to the old "remember it at each gRPC call site" gap.
/// </summary>
public class MessageEnqueueLiveTriggerTests
{
    [Fact]
    public async Task ServerEnqueue_RaisesMessageEnqueuedEvent_ForTheRecipient()
    {
        var bus = new InProcessEventBus();
        var recipients = new List<int>();
        bus.Subscribe<MessageEnqueuedEvent>(e => recipients.Add(e.RecipientCharacterId));

        var handler = new EnqueueMessageCommandHandler(
            new FakeMessageRepository(messageId: 99), new FakeRuntimeContext(ExecutionHost.Server), bus);

        var result = await handler.Handle(new EnqueueMessageCommand(
            RecipientCharacterId: 7007, SenderCharacterId: 6001, MessageKind.Mail,
            "Fleet started: Test", "Open its metrics.", null, null), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(99, result.Value);
        Assert.Equal([7007], recipients);
    }

    [Fact]
    public async Task ClientEnqueue_IsANoOp_AndRaisesNoEvent()
    {
        var bus = new InProcessEventBus();
        var count = 0;
        bus.Subscribe<MessageEnqueuedEvent>(_ => count++);

        var handler = new EnqueueMessageCommandHandler(
            new FakeMessageRepository(messageId: 99), new FakeRuntimeContext(ExecutionHost.Client), bus);

        var result = await handler.Handle(new EnqueueMessageCommand(
            RecipientCharacterId: 7007, SenderCharacterId: null, MessageKind.Mail, "x", null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(0L, result.Value); // client has no inbox queue → no-op success
        Assert.Equal(0, count);         // and nothing is raised on the bus
    }

    private sealed class FakeRuntimeContext(ExecutionHost host) : IRuntimeContext
    {
        public ExecutionHost Host => host;
    }

    private sealed class FakeMessageRepository(long messageId) : IMessageRepository
    {
        public Task<long> AddAsync(QueuedMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(messageId);

        public Task<QueuedMessage?> GetAsync(long id, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task UpdateAsync(QueuedMessage message, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<IReadOnlyList<QueuedMessage>> ListPendingForRecipientAsync(int recipient, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<int> DeleteExpiredAsync(System.DateTimeOffset asOf, CancellationToken ct = default) => throw new System.NotSupportedException();
    }
}
