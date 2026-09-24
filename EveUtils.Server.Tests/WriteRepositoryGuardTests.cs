using EveUtils.Server.Grpc;
using EveUtils.Server.Messaging;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-383 (epic ET-379): a write that does not go through a command handler publishes no change signal, so no screen,
/// relay or other client hears it. <c>CommandSignalCoverageTests</c> holds every command to the signal; this holds the
/// shared and server code to the commands: nothing else takes a write repository in its constructor, unless it is on the
/// list below with the reason. The client's own types are held in <c>EveUtils.Client.UiTests</c>.
/// </summary>
public sealed class WriteRepositoryGuardTests
{
    /// <summary>Types that write past the command handlers on purpose. An entry here is a claim a reviewer has to agree
    /// with, not a way to make this test pass.</summary>
    private static readonly IReadOnlyDictionary<Type, string> Allowed = new Dictionary<Type, string>
    {
        // ET-222: the server's run sync applier. It stores a run a client pushed — the client's own command already
        // signalled it there — and relays it to the group itself; the Runs signal describes the client's database.
        [typeof(RunsGrpcService)] = "the server's run sync applier (ET-222): it stores a run a client already changed "
            + "through its own command, and relays the run to the group itself",
        // ET-381: runs inside RespondToJoinRequestCommand and RespondToMessageCommand, and publishes FleetChangedEvent
        // itself after the roster write; it is the shared core of both handlers, not a way around them.
        [typeof(JoinRequestResponder)] = "the shared core of the RespondToJoinRequest and RespondToMessage handlers, "
            + "called from inside them; it publishes FleetChangedEvent itself after its write",
        // Delivery bookkeeping: marks a pushed invite delivered, drops a pushed mail. What a player sees of it is the
        // delivery itself, which this service sends; nothing lists the queue.
        [typeof(MessageDeliveryService)] = "delivery bookkeeping: it marks a pushed invite delivered or drops a pushed "
            + "mail, and the push it just sent is the news; no screen lists the queue"
    };

    [Fact]
    public void NoTypeOutsideTheCommandHandlers_TakesAWriteRepository_WithoutAReason()
    {
        Dictionary<Type, Type[]> takers = new[] { typeof(ICommandHandler<>).Assembly, typeof(RunsGrpcService).Assembly }
            .SelectMany(assembly => WriteRepositoryGuard.TakersOutsideCommandHandlers(assembly))
            .ToDictionary();

        string[] unexplained = [.. takers
            .Where(taker => !Allowed.ContainsKey(taker.Key))
            .Select(taker => $"{taker.Key.Name} ({string.Join(", ", taker.Value.Select(repository => repository.Name))})")];
        Assert.True(unexplained.Length == 0,
            $"These take a write repository outside a command handler: {string.Join("; ", unexplained)}. Write through a "
            + "command, so its signal is published, and take the repository's reader half for the reads.");
        Assert.All(Allowed, allowed => Assert.True(takers.ContainsKey(allowed.Key),
            $"{allowed.Key.Name} no longer takes a write repository; take it off the list."));
        Assert.All(Allowed, allowed => Assert.False(string.IsNullOrWhiteSpace(allowed.Value)));
    }

    [Fact]
    public void Guard_FlagsATypeTakingAWriteRepository_AndLetsAReaderPass()
    {
        IReadOnlyDictionary<Type, Type[]> takers = WriteRepositoryGuard.TakersOutsideCommandHandlers(typeof(WriteRepositoryGuardTests).Assembly);

        Assert.Equal([typeof(IFleetRepository)], takers[typeof(WritingProbe)]);
        Assert.DoesNotContain(typeof(ReadingProbe), takers.Keys);
    }

    private sealed class WritingProbe(IFleetRepository fleets)
    {
        public IFleetRepository Fleets => fleets;
    }

    private sealed class ReadingProbe(IFleetReader fleets)
    {
        public IFleetReader Fleets => fleets;
    }
}
