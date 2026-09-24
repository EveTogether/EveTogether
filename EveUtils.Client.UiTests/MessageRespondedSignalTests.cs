using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-382: answering a queued message is announced once the responder and the status update are done, and never for
/// an answer that was refused or did not take.
/// </summary>
public sealed class MessageRespondedSignalTests
{
    private const int Recipient = 7007;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Respond_ToAnOpenInvite_PublishesMessageRespondedWithTheAnswer()
    {
        (RespondToMessageCommandHandler handler, List<MessageRespondedEvent> heard, FakeMessageRepository repository) = _Arrange();

        Result<EveUtils.Shared.Modules.Messaging.Dtos.MessageResponsePayload> result =
            await handler.Handle(new RespondToMessageCommand(99, true, Recipient), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageStatus.Responded, repository.Message.Status);
        MessageRespondedEvent published = Assert.Single(heard);
        Assert.Equal((99L, MessageKind.FleetInvite, true), (published.Data.MessageId, published.Data.Kind, published.Data.Accepted));
    }

    [Fact]
    public async Task Respond_ByAnyoneButTheRecipient_PublishesNothing()
    {
        (RespondToMessageCommandHandler handler, List<MessageRespondedEvent> heard, _) = _Arrange();

        Result<EveUtils.Shared.Modules.Messaging.Dtos.MessageResponsePayload> result =
            await handler.Handle(new RespondToMessageCommand(99, true, Recipient + 1), Ct);

        Assert.False(result.IsSuccess);
        Assert.Empty(heard);
    }

    [Fact]
    public async Task Respond_WhenTheResponderRefuses_PublishesNothing()
    {
        (RespondToMessageCommandHandler handler, List<MessageRespondedEvent> heard, FakeMessageRepository repository) =
            _Arrange(responderSucceeds: false);

        Result<EveUtils.Shared.Modules.Messaging.Dtos.MessageResponsePayload> result =
            await handler.Handle(new RespondToMessageCommand(99, true, Recipient), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageStatus.Delivered, repository.Message.Status);
        Assert.Empty(heard);
    }

    private static (RespondToMessageCommandHandler Handler, List<MessageRespondedEvent> Heard, FakeMessageRepository Repository)
        _Arrange(bool responderSucceeds = true)
    {
        var bus = new InProcessEventBus();
        List<MessageRespondedEvent> heard = [];
        bus.Subscribe<MessageRespondedEvent>(published => heard.Add(published));
        var repository = new FakeMessageRepository(new QueuedMessage
        {
            Id = 99, RecipientCharacterId = Recipient, Kind = MessageKind.FleetInvite, Status = MessageStatus.Delivered, Title = "Invite"
        });
        return (new RespondToMessageCommandHandler(repository, [new FakeResponder(responderSucceeds)], bus), heard, repository);
    }

    private sealed class FakeResponder(bool succeeds) : IMessageResponder
    {
        public MessageKind Kind => MessageKind.FleetInvite;

        public Task<Result> RespondAsync(QueuedMessage message, bool accept, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(succeeds
                ? Result.Success()
                : Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, "Refused.", "Messaging")));
    }

    private sealed class FakeMessageRepository(QueuedMessage message) : IMessageRepository
    {
        public QueuedMessage Message => message;

        public Task<QueuedMessage?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult<QueuedMessage?>(message);

        public Task UpdateAsync(QueuedMessage updated, CancellationToken ct = default) => Task.CompletedTask;

        public Task<long> AddAsync(QueuedMessage added, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueuedMessage>> ListPendingForRecipientAsync(int recipient, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
