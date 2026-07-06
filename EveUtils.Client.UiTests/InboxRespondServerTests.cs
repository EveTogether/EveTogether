using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// a delivered message records the server it came from (ClientInboxMessage.ServerAddress), and answering it
/// must go back to that server — not the first coupled one (the old ListServersAsync().FirstOrDefault()). Drives the
/// real InboxViewModel over a faked transport (no gRPC) and asserts RespondToMessage targets the message's origin,
/// with a fallback to the first server only for rows stored before the origin was tracked (null ServerAddress).
/// </summary>
public class InboxRespondServerTests
{
    private const string ServerA = "srvA:7443"; // the only coupled session → ListServersAsync().FirstOrDefault()
    private const string ServerB = "srvB:7443"; // where the message actually came from
    private const int Recipient = 100;
    private const long MessageId = 42;

    [AvaloniaFact]
    public async Task Respond_GoesToTheMessageOriginServer_NotTheFirstCoupledOne()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IFleetTransportClient>(transport));

        // Only Server A has a session (so FirstOrDefault would pick it), but the message arrived from Server B.
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(ServerA, new ClientSessionTokens("t", "r", "Lionear", Recipient));
        await SeedInviteAsync(instance, originServer: ServerB);

        var vm = await BuildInboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: true);

        var call = Assert.Single(transport.RespondToMessageCalls);
        Assert.Equal(ServerB, call.ServerAddress); // the origin, not ServerA (the FirstOrDefault)
        Assert.Equal(MessageId, call.MessageId);
        Assert.True(call.Accept);
        Assert.Equal(Recipient, call.ActingCharacterId);
    }

    [AvaloniaFact]
    public async Task Respond_FallsBackToFirstServer_WhenOriginUnknown()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IFleetTransportClient>(transport));

        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(ServerA, new ClientSessionTokens("t", "r", "Lionear", Recipient));
        await SeedInviteAsync(instance, originServer: null); // a pre-column row

        var vm = await BuildInboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: false);

        var call = Assert.Single(transport.RespondToMessageCalls);
        Assert.Equal(ServerA, call.ServerAddress); // no origin → fall back to the only coupled server
    }

    private static Task SeedInviteAsync(TestClientInstance instance, string? originServer) =>
        instance.Services.GetRequiredService<IClientInboxStore>().UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = MessageId,
            ServerAddress = originServer,
            RecipientCharacterId = Recipient,
            Kind = MessageKind.FleetInvite,
            Title = "Join my fleet",
            CreatedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            Status = MessageStatus.Pending,
            IsRead = false,
        });

    private static async Task<InboxViewModel> BuildInboxAsync(TestClientInstance instance)
    {
        var vm = instance.Services.GetRequiredService<InboxViewModel>(); // ctor loads the seeded inbox
        for (var i = 0; i < 100 && vm.Messages.Count == 0; i++)
            await Task.Delay(50);
        Assert.NotEmpty(vm.Messages);
        return vm;
    }
}
