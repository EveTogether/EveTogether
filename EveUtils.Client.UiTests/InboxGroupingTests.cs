using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// merged inbox: when one fleet action is delivered to several of my multiboxed characters, the per-character
/// copies collapse into a single row that lists every recipient ("To: A, B") and fans a response/delete out to each
/// copy. Drives the real InboxViewModel over the throwaway client DI + a faked transport (no gRPC).
/// </summary>
public class InboxGroupingTests
{
    private const string ServerA = "srvA:7443";

    [AvaloniaFact]
    public async Task SameActionToManyCharacters_MergesIntoOneRow_ListingEveryRecipient()
    {
        using var instance = TestClientInstance.Create();
        await RegisterCharactersAsync(instance, ("Alpha", 100), ("Bravo", 200));

        // The same "Fleet started" notice delivered to two of my characters (distinct server-message ids).
        await SeedMailAsync(instance, serverMessageId: 1, recipient: 100);
        await SeedMailAsync(instance, serverMessageId: 2, recipient: 200);

        var vm = await BuildInboxAsync(instance);

        var row = Assert.Single(vm.Messages);
        Assert.Equal(2, row.LocalIds.Count);
        Assert.Equal("To: Alpha, Bravo", row.RecipientLabel);
    }

    [AvaloniaFact]
    public async Task DifferentActions_StayAsSeparateRows()
    {
        using var instance = TestClientInstance.Create();
        await RegisterCharactersAsync(instance, ("Alpha", 100));

        await SeedMailAsync(instance, serverMessageId: 1, recipient: 100, title: "Fleet started: Roam");
        await SeedMailAsync(instance, serverMessageId: 2, recipient: 100, title: "Fleet started: Mining");

        var vm = await BuildInboxAsync(instance);

        Assert.Equal(2, vm.Messages.Count);
    }

    [AvaloniaFact]
    public async Task Delete_RemovesEveryMergedCopy()
    {
        using var instance = TestClientInstance.Create();
        await RegisterCharactersAsync(instance, ("Alpha", 100), ("Bravo", 200));
        await SeedMailAsync(instance, serverMessageId: 1, recipient: 100);
        await SeedMailAsync(instance, serverMessageId: 2, recipient: 200);

        var vm = await BuildInboxAsync(instance);
        await vm.DeleteAsync(Assert.Single(vm.Messages));

        Assert.Empty(vm.Messages);
        Assert.True(vm.IsEmpty);
        Assert.Empty(await instance.Services.GetRequiredService<IClientInboxStore>().ListAllAsync());
    }

    [AvaloniaFact]
    public async Task RespondToMergedInvite_FansOutToEveryRecipient()
    {
        var transport = new RecordingFleetTransportClient();
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IFleetTransportClient>(transport));
        await RegisterCharactersAsync(instance, ("Alpha", 100), ("Bravo", 200));

        // The same fleet invites two of my characters → one merged row, two pending copies on the same server.
        await SeedInviteAsync(instance, serverMessageId: 42, recipient: 100);
        await SeedInviteAsync(instance, serverMessageId: 43, recipient: 200);

        var vm = await BuildInboxAsync(instance);
        await vm.RespondAsync(Assert.Single(vm.Messages), accept: true);

        Assert.Equal(2, transport.RespondToMessageCalls.Count);
        Assert.Contains(transport.RespondToMessageCalls, c => c.MessageId == 42 && c.ActingCharacterId == 100 && c.Accept);
        Assert.Contains(transport.RespondToMessageCalls, c => c.MessageId == 43 && c.ActingCharacterId == 200 && c.Accept);
    }

    [AvaloniaFact]
    public async Task ClearAll_WhenConfirmed_EmptiesTheInbox()
    {
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<EveUtils.Client.Dialogs.IDialogService>(dialogs));
        await RegisterCharactersAsync(instance, ("Alpha", 100));
        await SeedMailAsync(instance, serverMessageId: 1, recipient: 100, title: "Fleet started: Roam");
        await SeedMailAsync(instance, serverMessageId: 2, recipient: 100, title: "Fleet started: Mining");

        var vm = await BuildInboxAsync(instance);
        await vm.ClearAllCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.Equal("Clear inbox", dialogs.LastConfirmTitle);
    }

    private static Task RegisterCharactersAsync(TestClientInstance instance, params (string Name, int Id)[] characters)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        return Task.WhenAll(characters.Select(c => registry.AddOrUpdateAsync(new Character(c.Name, c.Id))));
    }

    private static Task SeedMailAsync(TestClientInstance instance, long serverMessageId, int recipient,
        string title = "Fleet started: Roam") =>
        instance.Services.GetRequiredService<IClientInboxStore>().UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = serverMessageId,
            ServerAddress = ServerA,
            RecipientCharacterId = recipient,
            Kind = MessageKind.Mail,
            Title = title,
            Body = "The fleet has started.",
            CreatedAt = System.DateTimeOffset.UnixEpoch,
            ReceivedAt = System.DateTimeOffset.UnixEpoch,
            Status = MessageStatus.Pending,
            IsRead = false,
        });

    private static Task SeedInviteAsync(TestClientInstance instance, long serverMessageId, int recipient) =>
        instance.Services.GetRequiredService<IClientInboxStore>().UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = serverMessageId,
            ServerAddress = ServerA,
            RecipientCharacterId = recipient,
            Kind = MessageKind.FleetInvite,
            Title = "Fleet invite: Roam",
            Body = "You are invited.",
            CreatedAt = System.DateTimeOffset.UnixEpoch,
            ReceivedAt = System.DateTimeOffset.UnixEpoch,
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
